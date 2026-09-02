using System;
using System.Collections.Generic;

namespace DepthView.Imaging;

public enum SampleKind
{
    /// <summary>Integer samples held in <see cref="ImageData.Samples"/>, range 0..MaxValue.</summary>
    UInt,
    /// <summary>Floating point samples held in <see cref="ImageData.Floats"/>.</summary>
    Float
}

/// <summary>
/// Everything we could learn about the container itself, as declared by the file,
/// independent of what the pixels actually turn out to contain.
/// </summary>
public sealed class ImageMetadata
{
    public string Format = "Unknown";
    public string ColorModel = "Unknown";
    public int DeclaredBitDepth;          // bits per sample as stored in the file
    public int DeclaredChannels;
    public bool HasAlpha;
    public bool IsPalette;
    public int PaletteSize;
    public bool Interlaced;
    public string? InterlaceMethod;
    public string? CompressionMethod;
    public string? FilterMethod;
    public int[]? SignificantBits;        // PNG sBIT chunk
    public double? Gamma;
    public bool HasIccProfile;
    public string? IccProfileName;
    public double? DpiX;
    public double? DpiY;
    public long FileBytes;
    public string? FileName;
    public string? FilePath;
    public string? SourceNote;            // "dropped", "browsed", "pasted", ...

    /// <summary>
    /// False when the decoder normalised or promoted the samples, so the reported
    /// bit depth cannot be trusted for imposter analysis.
    /// </summary>
    public bool BitDepthIsExact = true;

    public List<KeyValuePair<string, string>> Text = new();
    public List<string> Warnings = new();
}

/// <summary>Decoded pixels, kept at the file's native precision. No normalisation.</summary>
public sealed class ImageData
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>1 = grey, 2 = grey+alpha, 3 = RGB, 4 = RGBA.</summary>
    public required int Channels { get; init; }

    /// <summary>Bits per sample actually represented by the values in <see cref="Samples"/>.</summary>
    public required int BitDepth { get; init; }

    /// <summary>Largest legal sample value (65535 for 16-bit, 4095 for a 12-bit PGM, ...).</summary>
    public required int MaxValue { get; init; }

    public SampleKind Kind { get; init; } = SampleKind.UInt;

    /// <summary>Interleaved samples, length = Width * Height * Channels.</summary>
    public ushort[]? Samples { get; init; }

    /// <summary>Interleaved float samples for float formats (PFM).</summary>
    public float[]? Floats { get; init; }

    public long PixelCount => (long)Width * Height;

    /// <summary>
    /// The same image with its rows in the opposite order.
    ///
    /// Lossless by construction: rows are moved, never combined, so every sample value and the
    /// histogram built from them come out identical. That matters here more than it usually
    /// would - this program's entire argument is that nothing should quietly alter the levels
    /// in a depth map, and a flip that resampled would be exactly the sin it complains about.
    ///
    /// Needed because LightBurn stores an embedded bitmap bottom-up, its bed coordinates having
    /// Y increasing upward. Verified against two projects whose transforms disagreed about the
    /// Y sign: in both, the stored data was the source file flipped vertically and identical
    /// otherwise.
    /// </summary>
    public ImageData FlipVertical()
    {
        int stride = Width * Channels;

        ushort[]? samples = null;
        if (Samples is not null)
        {
            samples = new ushort[Samples.Length];
            for (int y = 0; y < Height; y++)
                Array.Copy(Samples, (long)y * stride,
                           samples, (long)(Height - 1 - y) * stride, stride);
        }

        float[]? floats = null;
        if (Floats is not null)
        {
            floats = new float[Floats.Length];
            for (int y = 0; y < Height; y++)
                Array.Copy(Floats, (long)y * stride,
                           floats, (long)(Height - 1 - y) * stride, stride);
        }

        return new ImageData
        {
            Width = Width,
            Height = Height,
            Channels = Channels,
            BitDepth = BitDepth,
            MaxValue = MaxValue,
            Kind = Kind,
            Samples = samples,
            Floats = floats
        };
    }

    public static void GuardSize(long width, long height, int channels)
    {
        long total = width * height * channels;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Image reports zero or negative dimensions.");
        if (total > 600_000_000)
            throw new InvalidOperationException(
                $"Image is too large for in-memory analysis ({width} x {height} x {channels} samples).");
    }
}
