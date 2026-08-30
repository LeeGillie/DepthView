using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DepthView.Imaging;

/// <summary>
/// Dependency-free PNG decoder written specifically so that 16-bit samples survive
/// untouched. Every general purpose imaging stack on Windows, macOS and Linux
/// (WIC, CoreGraphics, GDK, and browser canvas) will happily hand back an 8-bit
/// buffer for a 16-bit PNG, which would destroy the exact thing we are trying to
/// measure. So we do it ourselves.
///
/// Supports colour types 0/2/3/4/6, bit depths 1/2/4/8/16, Adam7 interlace,
/// and all five scanline filters.
/// </summary>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool Looks(ReadOnlySpan<byte> data)
        => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static ImageData Decode(byte[] file, ImageMetadata meta)
    {
        if (!Looks(file)) throw new InvalidDataException("Not a PNG file.");

        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        int compression = 0, filterMethod = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        bool sawIhdr = false;
        var idat = new MemoryStream();

        while (pos + 8 <= file.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos, 4));
            pos += 4;
            if (len < 0 || pos + 4 + len + 4 > file.Length) break;

            string type = Encoding.ASCII.GetString(file, pos, 4);
            pos += 4;
            var span = new ReadOnlySpan<byte>(file, pos, len);

            switch (type)
            {
                case "IHDR":
                    if (len < 13) throw new InvalidDataException("Truncated IHDR.");
                    width = BinaryPrimitives.ReadInt32BigEndian(span);
                    height = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
                    bitDepth = span[8];
                    colorType = span[9];
                    compression = span[10];
                    filterMethod = span[11];
                    interlace = span[12];
                    sawIhdr = true;
                    break;

                case "PLTE":
                    palette = span.ToArray();
                    break;

                case "tRNS":
                    trns = span.ToArray();
                    break;

                case "IDAT":
                    idat.Write(span);
                    break;

                case "gAMA":
                    if (len >= 4) meta.Gamma = BinaryPrimitives.ReadUInt32BigEndian(span) / 100000.0;
                    break;

                case "sBIT":
                {
                    var sig = new int[len];
                    for (int i = 0; i < len; i++) sig[i] = span[i];
                    meta.SignificantBits = sig;
                    break;
                }

                case "pHYs":
                    if (len >= 9)
                    {
                        uint px = BinaryPrimitives.ReadUInt32BigEndian(span);
                        uint py = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                        if (span[8] == 1 && px > 0 && py > 0)
                        {
                            meta.DpiX = Math.Round(px * 0.0254, 2);
                            meta.DpiY = Math.Round(py * 0.0254, 2);
                        }
                    }
                    break;

                case "iCCP":
                {
                    meta.HasIccProfile = true;
                    int z = span.IndexOf((byte)0);
                    if (z > 0) meta.IccProfileName = Encoding.Latin1.GetString(span[..z]);
                    break;
                }

                case "tEXt":
                {
                    int z = span.IndexOf((byte)0);
                    if (z > 0)
                        meta.Text.Add(new KeyValuePair<string, string>(
                            Encoding.Latin1.GetString(span[..z]),
                            Encoding.Latin1.GetString(span[(z + 1)..])));
                    break;
                }

                case "zTXt":
                {
                    int z = span.IndexOf((byte)0);
                    if (z > 0)
                        meta.Text.Add(new KeyValuePair<string, string>(
                            Encoding.Latin1.GetString(span[..z]), "(deflate-compressed text)"));
                    break;
                }

                case "iTXt":
                    ReadITxt(span, meta);
                    break;
            }

            if (type == "IEND") break;
            pos += len + 4; // payload + CRC
        }

        if (!sawIhdr) throw new InvalidDataException("PNG has no IHDR chunk.");
        if (compression != 0) throw new InvalidDataException($"Unsupported PNG compression method {compression}.");
        if (filterMethod != 0) throw new InvalidDataException($"Unsupported PNG filter method {filterMethod}.");
        if (interlace > 1) throw new InvalidDataException($"Unsupported PNG interlace method {interlace}.");
        if (idat.Length == 0) throw new InvalidDataException("PNG has no image data.");

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unsupported PNG colour type {colorType}.")
        };

        ValidateDepth(colorType, bitDepth);
        ImageData.GuardSize(width, height, channels);

        meta.Format = "PNG";
        meta.DeclaredBitDepth = bitDepth;
        meta.DeclaredChannels = channels;
        meta.ColorModel = colorType switch
        {
            0 => "Grayscale",
            2 => "RGB (truecolour)",
            3 => "Indexed (palette)",
            4 => "Grayscale + alpha",
            6 => "RGBA (truecolour + alpha)",
            _ => "Unknown"
        };
        meta.HasAlpha = colorType is 4 or 6 || (colorType == 3 && trns is { Length: > 0 });
        meta.IsPalette = colorType == 3;
        meta.PaletteSize = palette is null ? 0 : palette.Length / 3;
        meta.Interlaced = interlace == 1;
        meta.InterlaceMethod = interlace == 1 ? "Adam7" : "None";
        meta.CompressionMethod = "Deflate (zlib)";
        meta.FilterMethod = "Adaptive per-scanline";
        meta.BitDepthIsExact = true;

        byte[] raw = Inflate(idat, EstimateRawSize(width, height, channels, bitDepth, interlace == 1));

        var dest = new ushort[(long)width * height * channels];
        int offset = 0;

        if (interlace == 0)
        {
            ReadPass(raw, ref offset, width, height, bitDepth, channels, dest, width, 0, 0, 1, 1);
        }
        else
        {
            ReadOnlySpan<int> xs = stackalloc int[] { 0, 4, 0, 2, 0, 1, 0 };
            ReadOnlySpan<int> ys = stackalloc int[] { 0, 0, 4, 0, 2, 0, 1 };
            ReadOnlySpan<int> xd = stackalloc int[] { 8, 8, 4, 4, 2, 2, 1 };
            ReadOnlySpan<int> yd = stackalloc int[] { 8, 8, 8, 4, 4, 2, 2 };

            for (int p = 0; p < 7; p++)
            {
                int pw = (width - xs[p] + xd[p] - 1) / xd[p];
                int ph = (height - ys[p] + yd[p] - 1) / yd[p];
                if (pw <= 0 || ph <= 0) continue;
                ReadPass(raw, ref offset, pw, ph, bitDepth, channels, dest, width, xs[p], ys[p], xd[p], yd[p]);
            }
        }

        // Palette images: expand indices to RGB(A) so the analyser sees real colours.
        if (colorType == 3)
        {
            if (palette is null) throw new InvalidDataException("Palette PNG has no PLTE chunk.");
            bool alpha = trns is { Length: > 0 };
            int outCh = alpha ? 4 : 3;
            var expanded = new ushort[(long)width * height * outCh];
            long n = (long)width * height;
            int entries = palette.Length / 3;

            for (long i = 0; i < n; i++)
            {
                int idx = dest[i];
                long o = i * outCh;
                if (idx < entries)
                {
                    expanded[o] = palette[idx * 3];
                    expanded[o + 1] = palette[idx * 3 + 1];
                    expanded[o + 2] = palette[idx * 3 + 2];
                }
                if (alpha) expanded[o + 3] = (ushort)(idx < trns!.Length ? trns[idx] : 255);
            }

            meta.Warnings.Add(
                $"Palette image: {entries} palette entries expanded to 8-bit RGB. " +
                "Grey-level counts below refer to the expanded RGB values.");

            return new ImageData
            {
                Width = width, Height = height, Channels = outCh,
                BitDepth = 8, MaxValue = 255, Samples = expanded
            };
        }

        return new ImageData
        {
            Width = width, Height = height, Channels = channels,
            BitDepth = bitDepth, MaxValue = (1 << bitDepth) - 1, Samples = dest
        };
    }

    private static void ReadITxt(ReadOnlySpan<byte> span, ImageMetadata meta)
    {
        int z = span.IndexOf((byte)0);
        if (z <= 0 || span.Length < z + 3) return;

        string key = Encoding.Latin1.GetString(span[..z]);
        int p = z + 1;
        byte compressed = span[p];
        p += 2; // compression flag + compression method

        int lang = span[p..].IndexOf((byte)0);
        if (lang < 0) return;
        p += lang + 1;

        int trans = span[p..].IndexOf((byte)0);
        if (trans < 0) return;
        p += trans + 1;

        meta.Text.Add(new KeyValuePair<string, string>(
            key, compressed == 0 ? Encoding.UTF8.GetString(span[p..]) : "(deflate-compressed text)"));
    }

    private static void ValidateDepth(int colorType, int bitDepth)
    {
        bool ok = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };
        if (!ok) throw new InvalidDataException($"Illegal PNG bit depth {bitDepth} for colour type {colorType}.");
    }

    private static int EstimateRawSize(int w, int h, int ch, int bd, bool interlaced)
    {
        long stride = ((long)w * ch * bd + 7) / 8;
        long size = (stride + 1) * h;
        if (interlaced) size += (long)h * 8; // interlaced passes carry more filter bytes
        return (int)Math.Min(size + 1024, int.MaxValue - 64);
    }

    private static byte[] Inflate(MemoryStream idat, int hint)
    {
        idat.Position = 0;
        using var outMs = new MemoryStream(Math.Max(1024, hint));
        using (var z = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            z.CopyTo(outMs);
        }
        return outMs.ToArray();
    }

    private static void ReadPass(
        byte[] raw, ref int offset,
        int passW, int passH, int bitDepth, int channels,
        ushort[] dest, int destW,
        int xStart, int yStart, int xStep, int yStep)
    {
        int bpp = Math.Max(1, channels * bitDepth / 8);
        int stride = (passW * channels * bitDepth + 7) / 8;

        var prev = new byte[stride];
        var cur = new byte[stride];

        for (int y = 0; y < passH; y++)
        {
            if (offset >= raw.Length)
                throw new InvalidDataException("PNG image data ended early (truncated file?).");

            int filter = raw[offset++];
            int avail = Math.Min(stride, raw.Length - offset);
            if (avail < stride)
            {
                Array.Clear(cur);
                if (avail > 0) Buffer.BlockCopy(raw, offset, cur, 0, avail);
                offset = raw.Length;
            }
            else
            {
                Buffer.BlockCopy(raw, offset, cur, 0, stride);
                offset += stride;
            }

            Unfilter(filter, cur, prev, bpp, stride);

            int dy = yStart + y * yStep;
            long rowBase = (long)dy * destW;

            for (int x = 0; x < passW; x++)
            {
                int dx = xStart + x * xStep;
                long o = (rowBase + dx) * channels;
                int si = x * channels;
                for (int c = 0; c < channels; c++)
                    dest[o + c] = ReadSample(cur, si + c, bitDepth);
            }

            (prev, cur) = (cur, prev);
        }
    }

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp, int stride)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < stride; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + prev[i]);
                break;
            case 3:
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + ((a + prev[i]) >> 1));
                }
                break;
            case 4:
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + Paeth(a, b, c));
                }
                break;
            default:
                throw new InvalidDataException($"Unknown PNG scanline filter type {filter}.");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static ushort ReadSample(byte[] row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 16:
            {
                int i = index * 2;
                return i + 1 < row.Length ? (ushort)((row[i] << 8) | row[i + 1]) : (ushort)0;
            }
            case 8:
                return index < row.Length ? row[index] : (ushort)0;
            case 4:
            {
                int i = index >> 1;
                if (i >= row.Length) return 0;
                return (ushort)((index & 1) == 0 ? (row[i] >> 4) & 0x0F : row[i] & 0x0F);
            }
            case 2:
            {
                int i = index >> 2;
                if (i >= row.Length) return 0;
                int shift = 6 - 2 * (index & 3);
                return (ushort)((row[i] >> shift) & 0x03);
            }
            case 1:
            {
                int i = index >> 3;
                if (i >= row.Length) return 0;
                int shift = 7 - (index & 7);
                return (ushort)((row[i] >> shift) & 0x01);
            }
            default:
                throw new InvalidDataException($"Unsupported bit depth {bitDepth}.");
        }
    }
}
