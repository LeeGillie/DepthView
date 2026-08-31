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
    // A slicer turns the map into passes: darkest gets every pass, pure white gets none.
    // So the useful question is not how many levels the file holds in the abstract, but how
    // many distinct depths survive at the pass count you intend to run.
    //
    // The pass count is the parameter, deliberately. LightBurn's galvo path was internally
    // 8-bit before 2.1 and gained 16-bit support in it, and other toolchains have their own
    // ceilings - MakeIt is quoted at 256 layers. Hard-coding any one of those would bake a
    // particular version of a particular program into the analysis. Asking "at N passes,
    // what do I get" is true everywhere and stays true.

    /// <summary>
    /// Distinct depths this map produces at a given pass count, and how many of those passes
    /// repeat a depth that already exists.
    ///
    /// Both limits are real: you cannot resolve more depths than you have passes, and you
    /// cannot resolve more than the file holds once reduced to that many levels.
    /// </summary>
    public (int Distinct, int Wasted) SlicesAt(int passes)
    {
        if (passes <= 1 || UsedLevels.Length == 0) return (0, Math.Max(0, passes));
        int distinct = DistinctAfterReduction(UsedLevels, MaxValue, passes, MinLevel, MaxLevel, false);
        return (distinct, Math.Max(0, passes - distinct));
    }

    /// <summary>The same count after stretching the occupied range to fill the container.</summary>
    public int SlicesAtRemapped(int passes)
    {
        if (passes <= 1 || UsedLevels.Length == 0) return 0;
        return DistinctAfterReduction(UsedLevels, MaxValue, passes, MinLevel, MaxLevel, true);
    }

    /// <summary>Depths recovered purely by reclaiming unused headroom, at this pass count.</summary>
    public int SlicesLostToHeadroom(int passes) =>
        Math.Max(0, SlicesAtRemapped(passes) - SlicesAt(passes).Distinct);

    private static int DistinctAfterReduction(int[] used, int maxValue, int levels,
                                              int minLevel, int maxLevel, bool remap)
    {
        if (levels <= 1) return 0;
        double lo = remap ? minLevel : 0;
        double hi = remap ? maxLevel : maxValue;
        double span = hi - lo;
        if (span <= 0) return used.Length > 0 ? 1 : 0;

        var seen = new bool[levels];
        int distinct = 0;
        foreach (int v in used)
        {
            int q = (int)Math.Round((v - lo) / span * (levels - 1));
            if (q < 0) q = 0;
            if (q >= levels) q = levels - 1;
            if (!seen[q]) { seen[q] = true; distinct++; }
        }
        return distinct;
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
