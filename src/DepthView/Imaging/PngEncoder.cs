using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DepthView.Imaging;

/// <summary>
/// Writes single-channel greyscale PNG at 8 or 16 bits, and nothing else.
///
/// Deliberately narrow. A corrected depth map must come out as true greyscale at full
/// precision, because writing it as RGB would undo one of the faults this program exists
/// to report, and going through a general-purpose imaging library is how a 16-bit map
/// quietly becomes an 8-bit one. The decoder is hand-written for that reason; the encoder
/// is hand-written for the same one.
/// </summary>
public static class PngEncoder
{
    /// <param name="pixels">Row-major samples, length width*height.</param>
    /// <param name="bitDepth">8 or 16. At 8, values are written as-is and must already fit.</param>
    /// <param name="dpi">When set, written as a pHYs chunk so the importer knows the physical size.</param>
    /// <param name="text">Optional tEXt entries, e.g. what was done to the file and by what.</param>
    public static void WriteGrey(string path, ushort[] pixels, int width, int height,
                                 int bitDepth = 16, double? dpi = null,
                                 (string Key, string Value)[]? text = null)
    {
        using var fs = File.Create(path);
        WriteGrey(fs, pixels, width, height, bitDepth, dpi, text);
    }

    public static void WriteGrey(Stream output, ushort[] pixels, int width, int height,
                                 int bitDepth = 16, double? dpi = null,
                                 (string Key, string Value)[]? text = null)
    {
        if (bitDepth is not (8 or 16))
            throw new ArgumentOutOfRangeException(nameof(bitDepth), "Only 8 and 16 bit greyscale are written.");
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Image has no area.");
        if ((long)width * height > pixels.Length)
            throw new ArgumentException("Pixel buffer is smaller than the stated dimensions.");

        output.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = 0;    // colour type 0: greyscale, one channel
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace
        WriteChunk(output, "IHDR", ihdr);

        if (dpi is double d && d > 0)
        {
            // pHYs is in pixels per metre, so an importer can place the map at its true size
            // without the operator having to remember the scale.
            int ppm = (int)Math.Round(d / 0.0254);
            var phys = new byte[9];
            BinaryPrimitives.WriteInt32BigEndian(phys.AsSpan(0), ppm);
            BinaryPrimitives.WriteInt32BigEndian(phys.AsSpan(4), ppm);
            phys[8] = 1;   // unit: metres
            WriteChunk(output, "pHYs", phys);
        }

        if (text is not null)
            foreach (var (key, value) in text)
            {
                if (string.IsNullOrEmpty(key)) continue;
                // tEXt is Latin-1: keyword, NUL, text. Keep it to printable ASCII.
                var bytes = new MemoryStream();
                var k = Encoding.ASCII.GetBytes(Clip(key, 79));
                bytes.Write(k);
                bytes.WriteByte(0);
                bytes.Write(Encoding.ASCII.GetBytes(Clip(value ?? "", 4000)));
                WriteChunk(output, "tEXt", bytes.ToArray());
            }

        int bytesPerPixel = bitDepth == 16 ? 2 : 1;
        int stride = width * bytesPerPixel;
        var raw = new byte[(long)height * (stride + 1) <= int.MaxValue
            ? height * (stride + 1)
            : throw new ArgumentException("Image is too large to encode in one buffer.")];

        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;   // filter type 0 (None). Depth maps are smooth; the gain from
                            // per-scanline filter selection is not worth the risk of a bug
                            // in a file someone is about to cut metal from.
            int row = y * width;
            if (bitDepth == 16)
                for (int x = 0; x < width; x++)
                {
                    ushort v = pixels[row + x];
                    raw[o++] = (byte)(v >> 8);
                    raw[o++] = (byte)(v & 0xFF);
                }
            else
                for (int x = 0; x < width; x++)
                    raw[o++] = (byte)pixels[row + x];
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            compressed = ms.ToArray();
        }
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static void WriteChunk(Stream output, string tag, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        output.Write(len);

        var tagBytes = Encoding.ASCII.GetBytes(tag);
        output.Write(tagBytes);
        output.Write(data);

        uint crc = Crc32(tagBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte v in a) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        foreach (byte v in b) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
