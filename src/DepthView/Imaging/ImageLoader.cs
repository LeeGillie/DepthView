using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DepthView.Imaging;

/// <summary>
/// Picks a decoder. Formats where exact sample precision matters for depth-map work
/// (PNG, PGM, PFM) get purpose-written decoders; everything else falls through to
/// ImageSharp, which is fully managed and therefore ships unchanged to every platform.
/// </summary>
public static class ImageLoader
{
    public const string SupportedPatterns =
        "*.png;*.tif;*.tiff;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.tga;*.pgm;*.ppm;*.pbm;*.pnm;*.pfm;*.qoi";

    public static (ImageData Image, ImageMetadata Meta) Load(
        byte[] bytes, string? fileName, string? filePath, string sourceNote)
    {
        var meta = new ImageMetadata
        {
            FileBytes = bytes.LongLength,
            FileName = fileName,
            FilePath = filePath,
            SourceNote = sourceNote
        };

        if (bytes.Length < 8) throw new InvalidDataException("File is too small to be an image.");

        if (PngDecoder.Looks(bytes)) return (PngDecoder.Decode(bytes, meta), meta);
        if (PfmDecoder.Looks(bytes)) return (PfmDecoder.Decode(bytes, meta), meta);
        if (PnmDecoder.Looks(bytes)) return (PnmDecoder.Decode(bytes, meta), meta);

        return (LoadViaImageSharp(bytes, meta), meta);
    }

    private static ImageData LoadViaImageSharp(byte[] bytes, ImageMetadata meta)
    {
        using var ms = new MemoryStream(bytes, writable: false);

        ImageInfo info;
        try
        {
            info = Image.Identify(ms);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "Unrecognised image format. DepthView reads PNG, TIFF, JPEG, BMP, WebP, GIF, TGA, QOI, " +
                "Netpbm (PGM/PPM/PBM) and PFM. " + ex.Message);
        }

        ms.Position = 0;
        string format = info.Metadata.DecodedImageFormat?.Name ?? "Unknown";

        int declaredBits = 8;
        bool exact = true;
        var tiff = TiffSniffer.Sniff(bytes);

        if (tiff is not null)
        {
            if (tiff.BigTiff)
                throw new InvalidDataException("BigTIFF files are not supported.");

            declaredBits = tiff.BitsPerSample;
            meta.ColorModel = $"{tiff.PhotometricName}, {tiff.SamplesPerPixel} sample(s)/pixel";
            meta.CompressionMethod = tiff.CompressionName;
            meta.Warnings.Add($"TIFF sample format: {tiff.SampleFormatName}.");
            if (tiff.SampleFormat == 3)
                throw new InvalidDataException(
                    "Floating point TIFF is not supported. Convert to 16-bit PNG or PFM first.");
        }
        else
        {
            meta.ColorModel = format switch
            {
                "JPEG" => "YCbCr -> RGB, 8 bits/sample",
                "BMP" => "RGB",
                "WebP" => "RGB(A)",
                "GIF" => "Indexed (palette)",
                _ => "RGB(A)"
            };
            meta.CompressionMethod = format;
        }

        if (declaredBits is not (8 or 16))
        {
            meta.Warnings.Add(
                $"File declares {declaredBits} bits/sample; the decoder promoted samples to " +
                (declaredBits > 8 ? "16" : "8") + " bit. Imposter detection is unreliable for this file.");
            exact = false;
        }

        bool wide = declaredBits > 8;

        Image<Rgba64> img;
        try
        {
            img = Image.Load<Rgba64>(ms);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not decode this {format} file: {ex.Message}");
        }

        using (img)
        {
            int w = img.Width, h = img.Height;
            ImageData.GuardSize(w, h, 4);

            var rgba = new ushort[(long)w * h * 4];
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    long o = (long)y * w * 4;
                    for (int x = 0; x < row.Length; x++)
                    {
                        var px = row[x];
                        rgba[o] = px.R;
                        rgba[o + 1] = px.G;
                        rgba[o + 2] = px.B;
                        rgba[o + 3] = px.A;
                        o += 4;
                    }
                }
            });

            // ImageSharp promotes 8-bit sources to 16 bit by multiplying by 257.
            // Undo that exactly so the analyser sees the file's real values.
            if (!wide)
                for (long i = 0; i < rgba.LongLength; i++)
                    rgba[i] = (ushort)(rgba[i] / 257);

            ushort opaque = wide ? (ushort)65535 : (ushort)255;
            bool hasAlpha = false;
            for (long i = 3; i < rgba.LongLength; i += 4)
                if (rgba[i] != opaque) { hasAlpha = true; break; }

            int channels = hasAlpha ? 4 : 3;
            ushort[] samples;
            if (hasAlpha)
            {
                samples = rgba;
            }
            else
            {
                samples = new ushort[(long)w * h * 3];
                long s = 0, d = 0;
                for (long i = 0; i < (long)w * h; i++)
                {
                    samples[d] = rgba[s];
                    samples[d + 1] = rgba[s + 1];
                    samples[d + 2] = rgba[s + 2];
                    s += 4; d += 3;
                }
            }

            meta.Format = format;
            meta.DeclaredBitDepth = declaredBits;
            meta.DeclaredChannels = tiff?.SamplesPerPixel ?? (hasAlpha ? 4 : 3);
            meta.HasAlpha = hasAlpha;
            meta.IsPalette = format == "GIF";
            meta.InterlaceMethod ??= "Unknown";
            meta.BitDepthIsExact = exact;
            meta.HasIccProfile = info.Metadata.IccProfile is not null;
            if (info.Metadata.HorizontalResolution > 0) meta.DpiX = Math.Round(info.Metadata.HorizontalResolution, 2);
            if (info.Metadata.VerticalResolution > 0) meta.DpiY = Math.Round(info.Metadata.VerticalResolution, 2);

            if (info.Metadata.ExifProfile is not null)
                foreach (var v in info.Metadata.ExifProfile.Values)
                {
                    string? text = v.GetValue()?.ToString();
                    if (!string.IsNullOrWhiteSpace(text) && text.Length < 200)
                        meta.Text.Add(new(v.Tag.ToString(), text));
                }

            int bits = wide ? 16 : 8;
            return new ImageData
            {
                Width = w, Height = h, Channels = channels,
                BitDepth = bits, MaxValue = (1 << bits) - 1, Samples = samples
            };
        }
    }
}
