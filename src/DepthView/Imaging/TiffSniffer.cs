using System;
using System.Buffers.Binary;

namespace DepthView.Imaging;

/// <summary>
/// Reads just enough of a TIFF's first IFD to learn what the file *declares*.
/// General purpose decoders normalise TIFF samples to 8 or 16 bit, so without this
/// we could not tell a true 16-bit depth TIFF from an 8-bit one that got promoted.
/// </summary>
public sealed class TiffInfo
{
    public int Width;
    public int Height;
    public int BitsPerSample = 8;
    public int SamplesPerPixel = 1;
    public int Photometric = -1;
    public int Compression = -1;
    public int SampleFormat = 1;   // 1 = uint, 2 = int, 3 = IEEE float
    public bool BigTiff;

    public string PhotometricName => Photometric switch
    {
        0 => "Grayscale (0 = white)",
        1 => "Grayscale (0 = black)",
        2 => "RGB",
        3 => "Palette",
        4 => "Transparency mask",
        5 => "CMYK",
        6 => "YCbCr",
        8 => "CIE L*a*b*",
        _ => "Unknown"
    };

    public string CompressionName => Compression switch
    {
        1 => "None",
        2 => "CCITT Group 3 (1D)",
        3 => "CCITT T.4",
        4 => "CCITT T.6",
        5 => "LZW",
        6 => "JPEG (old style)",
        7 => "JPEG",
        8 => "Deflate (Adobe)",
        32773 => "PackBits",
        32946 => "Deflate",
        _ => Compression < 0 ? "Unknown" : $"Code {Compression}"
    };

    public string SampleFormatName => SampleFormat switch
    {
        1 => "Unsigned integer",
        2 => "Signed integer",
        3 => "IEEE floating point",
        _ => "Undefined"
    };
}

public static class TiffSniffer
{
    public static bool Looks(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return false;
        bool le = d[0] == 'I' && d[1] == 'I';
        bool be = d[0] == 'M' && d[1] == 'M';
        if (!le && !be) return false;
        int magic = le ? BinaryPrimitives.ReadUInt16LittleEndian(d[2..])
                       : BinaryPrimitives.ReadUInt16BigEndian(d[2..]);
        return magic is 42 or 43;
    }

    public static TiffInfo? Sniff(byte[] d)
    {
        try
        {
            if (!Looks(d)) return null;
            bool le = d[0] == 'I';
            var info = new TiffInfo();
            int magic = R16(d, 2, le);
            if (magic == 43) { info.BigTiff = true; return info; } // BigTIFF: report and stop

            int ifd = (int)R32(d, 4, le);
            if (ifd <= 0 || ifd + 2 > d.Length) return info;

            int entries = R16(d, ifd, le);
            for (int i = 0; i < entries; i++)
            {
                int e = ifd + 2 + i * 12;
                if (e + 12 > d.Length) break;

                int tag = R16(d, e, le);
                int type = R16(d, e + 2, le);
                long count = R32(d, e + 4, le);

                switch (tag)
                {
                    case 256: info.Width = (int)ReadValue(d, e + 8, type, le); break;
                    case 257: info.Height = (int)ReadValue(d, e + 8, type, le); break;
                    case 259: info.Compression = (int)ReadValue(d, e + 8, type, le); break;
                    case 262: info.Photometric = (int)ReadValue(d, e + 8, type, le); break;
                    case 277: info.SamplesPerPixel = (int)ReadValue(d, e + 8, type, le); break;
                    case 258: info.BitsPerSample = (int)ReadArrayFirst(d, e + 8, type, count, le); break;
                    case 339: info.SampleFormat = (int)ReadArrayFirst(d, e + 8, type, count, le); break;
                }
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    private static long ReadArrayFirst(byte[] d, int valueField, int type, long count, bool le)
    {
        int size = TypeSize(type);
        if (size * count <= 4) return ReadValue(d, valueField, type, le);
        int off = (int)R32(d, valueField, le);
        return off + size <= d.Length ? ReadValue(d, off, type, le) : 0;
    }

    private static long ReadValue(byte[] d, int off, int type, bool le) => type switch
    {
        1 or 2 or 6 or 7 => off < d.Length ? d[off] : 0,
        3 or 8 => off + 2 <= d.Length ? R16(d, off, le) : 0,
        4 or 9 => off + 4 <= d.Length ? R32(d, off, le) : 0,
        _ => 0
    };

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 1
    };

    private static int R16(byte[] d, int o, bool le)
        => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2))
              : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));

    private static long R32(byte[] d, int o, bool le)
        => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4))
              : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));
}
