using System;
using System.Collections.Generic;
using DepthView.Imaging;

namespace DepthView.Analysis;

public enum Severity { Good, Info, Warn, Alert }

public sealed record Finding(Severity Severity, string Title, string Detail);

public enum ImposterKind
{
    None,
    /// <summary>Every level is v * 257 - an 8-bit map replicated into 16 bits.</summary>
    Replicated257,
    /// <summary>Every level has a zero low byte - an 8-bit map shifted into the high byte.</summary>
    HighByteOnly,
    /// <summary>Levels form an evenly spaced ladder with a step greater than 1.</summary>
    QuantisedLadder,
    /// <summary>Far fewer distinct levels than the container can hold, with no clean pattern.</summary>
    SparseLevels
}

public sealed class AnalysisResult
{
    public ImageMetadata Meta = null!;

    public int Width, Height, Channels, BitDepth, MaxValue;
    public long PixelCount;
    public bool IsFloat;

    // --- grey vs colour -------------------------------------------------
    public long GreyPixels;                 // r == g == b (all pixels, for single-channel images)
    public long NonGreyPixels;
    public int UniqueGreyLevels;
    public long UniqueNonGreyColors;
    public bool NonGreyColorsCapped;
    public long UniqueColorsTotal;
    public bool TotalColorsCapped;
    public bool IsGrayscaleStoredAsColor;

    // --- histograms -----------------------------------------------------
    /// <summary>Counts per grey level, index 0..MaxValue. Only pixels where r == g == b.</summary>
    public long[] GreyHistogram = Array.Empty<long>();
    public long[]? HistR, HistG, HistB;
    public int UniqueR, UniqueG, UniqueB;
    public bool HistogramIsBinned;          // true for float images
    public float FloatMin, FloatMax;

    // --- statistics over grey levels ------------------------------------
    public int MinLevel, MaxLevel;

    // --- endpoints and clipping ----------------------------------------
    /// <summary>Pixels sitting exactly on the container maximum (pure white).</summary>
    public long PureWhitePixels;
    /// <summary>Pixels sitting exactly on zero (pure black).</summary>
    public long PureBlackPixels;
    /// <summary>Lightest level that actually occurs, and how many pixels are on it.</summary>
    public int LightestLevel;
    public long LightestCount;
    /// <summary>Darkest level that actually occurs, and how many pixels are on it.</summary>
    public int DarkestLevel;
    public long DarkestCount;
    /// <summary>Levels left unused above the lightest / below the darkest value present.</summary>
    public int HeadroomTop;
    public int HeadroomBottom;

    public double Mean, Median, StdDev;
    public int P1, P99;
    public List<(int Level, long Count)> TopLevels = new();

    // --- level structure ------------------------------------------------
    public int[] UsedLevels = Array.Empty<int>();
    public int LevelStep = 1;
    public bool UniformLadder;
    public int GapCount;
    public int LargestGap;
    public double Occupancy;                // unique / (MaxValue + 1)
    public double RangeUtilisation;         // (max - min + 1) / (MaxValue + 1)
    public int EffectiveBits;

    // --- slicing ---------------------------------------------------------
    // A depth-map slicer thresholds the image into passes. LightBurn's 3D Slice works in
    // 256 levels: one pass per grey level at 256 passes, batched below that, and duplicated
    // above it. So the number that decides how many passes are worth running is not how many
    // levels the file holds - it is how many survive reduction to 256.

    /// <summary>Distinct levels left after reducing to 256, i.e. slices that do real work.</summary>
    public int UsableSlices;

    /// <summary>What UsableSlices would become if the occupied range were stretched to fill the container.</summary>
    public int UsableSlicesRemapped;

    /// <summary>Slices lost purely to unused headroom: UsableSlicesRemapped - UsableSlices.</summary>
    public int SlicesLostToHeadroom => Math.Max(0, UsableSlicesRemapped - UsableSlices);

    /// <summary>
    /// Distinct slices actually produced at a given pass count, and how many of those passes
    /// repeat a slice that already exists. Below 256 the slicer batches levels together, so
    /// the count is limited by the passes; above it, by the data.
    /// </summary>
    public (int Distinct, int Wasted) SlicesAt(int passes)
    {
        if (passes <= 0 || UsableSlices <= 0) return (0, Math.Max(0, passes));
        int distinct = Math.Min(passes, UsableSlices);
        return (distinct, passes - distinct);
    }

    // --- alpha ----------------------------------------------------------
    public bool HasAlphaChannel;
    public int AlphaMin, AlphaMax;
    public bool AlphaConstant;

    // --- verdict --------------------------------------------------------
    public ImposterKind Imposter = ImposterKind.None;
    public string Verdict = "";
    public string VerdictDetail = "";
    public Severity VerdictSeverity = Severity.Info;
    public List<Finding> Findings = new();

    public TimeSpan Elapsed;

    public string DimensionText => $"{Width} x {Height}  ({PixelCount:N0} pixels)";
}
