using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace DepthView.Imaging;

/// <summary>
/// Portable Float Map reader. Monocular-depth networks (MiDaS, DPT, Depth Anything
/// and friends) frequently write raw inverse-depth as .pfm, and those files have no
/// discrete grey levels at all, so the analyser treats them separately.
/// </summary>
public static class PfmDecoder
{
    public static bool Looks(ReadOnlySpan<byte> d)
        => d.Length > 3 && d[0] == (byte)'P' && (d[1] == (byte)'F' || d[1] == (byte)'f')
           && (d[2] == (byte)'\n' || d[2] == (byte)'\r' || d[2] == (byte)' ');

    public static ImageData Decode(byte[] file, ImageMetadata meta)
    {
        int p = 0;
        string magic = ReadToken(file, ref p);
        int channels = magic switch
        {
            "PF" => 3,
            "Pf" => 1,
            _ => throw new InvalidDataException("Not a PFM file.")
        };

        int width = int.Parse(ReadToken(file, ref p), CultureInfo.InvariantCulture);
        int height = int.Parse(ReadToken(file, ref p), CultureInfo.InvariantCulture);
        double scale = double.Parse(ReadToken(file, ref p), CultureInfo.InvariantCulture);
        bool little = scale < 0;

        if (p < file.Length && (file[p] == '\n' || file[p] == '\r')) p++;

        ImageData.GuardSize(width, height, channels);

        long count = (long)width * height * channels;
        var floats = new float[count];
        int rowFloats = width * channels;

        // PFM stores scanlines bottom-to-top.
        for (int y = 0; y < height; y++)
        {
            int dy = height - 1 - y;
            for (int i = 0; i < rowFloats; i++)
            {
                int off = p + (y * rowFloats + i) * 4;
                if (off + 4 > file.Length) break;
                var span = file.AsSpan(off, 4);
                uint bits = little
                    ? BinaryPrimitives.ReadUInt32LittleEndian(span)
                    : BinaryPrimitives.ReadUInt32BigEndian(span);
                floats[(long)dy * rowFloats + i] = BitConverter.UInt32BitsToSingle(bits);
            }
        }

        meta.Format = "PFM (portable float map)";
        meta.ColorModel = channels == 3 ? "RGB float32" : "Grayscale float32";
        meta.DeclaredBitDepth = 32;
        meta.DeclaredChannels = channels;
        meta.CompressionMethod = "None (raw)";
        meta.InterlaceMethod = "None";
        meta.BitDepthIsExact = true;
        meta.Warnings.Add("Float format: samples are continuous, so \"unique grey levels\" counts " +
                          "distinct float values and the histogram is binned rather than exact.");

        return new ImageData
        {
            Width = width, Height = height, Channels = channels,
            BitDepth = 32, MaxValue = 0, Kind = SampleKind.Float, Floats = floats
        };
    }

    private static string ReadToken(byte[] d, ref int p)
    {
        var sb = new StringBuilder();
        while (p < d.Length && (d[p] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) p++;
        while (p < d.Length && d[p] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        {
            sb.Append((char)d[p]);
            p++;
        }
        if (sb.Length == 0) throw new InvalidDataException("Malformed PFM header.");
        return sb.ToString();
    }
}
