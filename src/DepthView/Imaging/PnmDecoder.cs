using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DepthView.Imaging;

/// <summary>
/// Netpbm (PBM/PGM/PPM) reader. Depth pipelines emit these constantly because the
/// format is trivial, and 16-bit P5 files are a very common depth-map carrier.
/// Handles P1..P6 and any MAXVAL, including non power-of-two ones like 4095.
/// </summary>
public static class PnmDecoder
{
    public static bool Looks(ReadOnlySpan<byte> d)
        => d.Length > 2 && d[0] == (byte)'P' && d[1] >= (byte)'1' && d[1] <= (byte)'6';

    public static ImageData Decode(byte[] file, ImageMetadata meta)
    {
        int p = 0;
        string magic = ReadToken(file, ref p);
        if (magic.Length != 2 || magic[0] != 'P') throw new InvalidDataException("Not a Netpbm file.");
        int kind = magic[1] - '0';

        int width = int.Parse(ReadToken(file, ref p), CultureInfo.InvariantCulture);
        int height = int.Parse(ReadToken(file, ref p), CultureInfo.InvariantCulture);
        int maxVal = kind is 1 or 4 ? 1 : int.Parse(ReadToken(file, ref p), CultureInfo.InvariantCulture);

        if (maxVal < 1 || maxVal > 65535) throw new InvalidDataException($"Illegal Netpbm MAXVAL {maxVal}.");

        int channels = kind is 3 or 6 ? 3 : 1;
        ImageData.GuardSize(width, height, channels);

        // For binary variants exactly one whitespace byte follows the header.
        if (kind >= 4 && p < file.Length) p++;

        int bits = BitsFor(maxVal);
        var samples = new ushort[(long)width * height * channels];
        long count = samples.LongLength;

        switch (kind)
        {
            case 1: // ASCII bitmap: 1 = black
                for (long i = 0; i < count; i++)
                    samples[i] = (ushort)(ReadAsciiInt(file, ref p) == 0 ? 1 : 0);
                break;

            case 2:
            case 3: // ASCII grey / RGB
                for (long i = 0; i < count; i++)
                    samples[i] = (ushort)Math.Clamp(ReadAsciiInt(file, ref p), 0, maxVal);
                break;

            case 4: // binary bitmap, packed MSB first, 1 = black
            {
                int stride = (width + 7) / 8;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int idx = p + y * stride + (x >> 3);
                        int bit = idx < file.Length ? (file[idx] >> (7 - (x & 7))) & 1 : 0;
                        samples[(long)y * width + x] = (ushort)(bit == 0 ? 1 : 0);
                    }
                break;
            }

            case 5:
            case 6: // binary grey / RGB
            {
                bool wide = maxVal > 255;
                for (long i = 0; i < count; i++)
                {
                    if (wide)
                    {
                        samples[i] = p + 1 < file.Length ? (ushort)((file[p] << 8) | file[p + 1]) : (ushort)0;
                        p += 2;
                    }
                    else
                    {
                        samples[i] = p < file.Length ? file[p] : (ushort)0;
                        p++;
                    }
                }
                break;
            }

            default:
                throw new InvalidDataException($"Unsupported Netpbm variant P{kind}.");
        }

        meta.Format = kind switch
        {
            1 => "PBM (ASCII bitmap)",
            2 => "PGM (ASCII grayscale)",
            3 => "PPM (ASCII RGB)",
            4 => "PBM (binary bitmap)",
            5 => "PGM (binary grayscale)",
            6 => "PPM (binary RGB)",
            _ => "Netpbm"
        };
        meta.ColorModel = channels == 3 ? "RGB" : "Grayscale";
        meta.DeclaredBitDepth = bits;
        meta.DeclaredChannels = channels;
        meta.CompressionMethod = "None (raw)";
        meta.InterlaceMethod = "None";
        meta.BitDepthIsExact = true;

        if (maxVal != (1 << bits) - 1)
            meta.Warnings.Add($"MAXVAL is {maxVal}, which is not a full {bits}-bit range. " +
                              "Level counts are reported against the declared MAXVAL.");

        return new ImageData
        {
            Width = width, Height = height, Channels = channels,
            BitDepth = bits, MaxValue = maxVal, Samples = samples
        };
    }

    private static int BitsFor(int maxVal)
    {
        int bits = 1;
        while ((1 << bits) - 1 < maxVal && bits < 16) bits++;
        return bits;
    }

    private static string ReadToken(byte[] d, ref int p)
    {
        var sb = new StringBuilder();
        while (p < d.Length)
        {
            byte b = d[p];
            if (b == (byte)'#') { while (p < d.Length && d[p] != '\n') p++; continue; }
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { p++; continue; }
            break;
        }
        while (p < d.Length)
        {
            byte b = d[p];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') break;
            sb.Append((char)b);
            p++;
        }
        if (sb.Length == 0) throw new InvalidDataException("Malformed Netpbm header.");
        return sb.ToString();
    }

    private static int ReadAsciiInt(byte[] d, ref int p)
    {
        while (p < d.Length)
        {
            byte b = d[p];
            if (b == (byte)'#') { while (p < d.Length && d[p] != '\n') p++; continue; }
            if (b < (byte)'0' || b > (byte)'9') { p++; continue; }
            break;
        }
        int v = 0;
        while (p < d.Length && d[p] >= (byte)'0' && d[p] <= (byte)'9')
        {
            v = v * 10 + (d[p] - '0');
            p++;
        }
        return v;
    }
}
