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
    /// add no new level.
    ///
    /// Both limits are real: you cannot resolve more depths than you have passes, and you
    /// cannot resolve more than the file holds once reduced to that many levels.
    ///
    /// The second figure is not a count of idle passes - see <see cref="PassesAt"/>, which
    /// splits them by what they actually do.
    /// </summary>
    public (int Distinct, int NoNewLevel) SlicesAt(int passes)
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

    /// <summary>Depths gained by stretching the occupied range to fill the container.</summary>
    public int SlicesLostToHeadroom(int passes) =>
        Math.Max(0, SlicesAtRemapped(passes) - SlicesAt(passes).Distinct);

    /// <summary>
    /// What the passes in a job actually do, split three ways.
    ///
    /// This replaces a single figure that used to be labelled "wasted", which was wrong and
    /// was rightly picked apart on the LightBurn forum by Finn65. A slicer masks each pass by
    /// a threshold; if two consecutive thresholds fall in a gap where no pixel value exists,
    /// the second pass fires on exactly the same mask as the first. It still cuts. It still
    /// removes material. What it does not do is create a new distinguishable step. Calling
    /// that "wasted" implies an idle laser, and the laser is not idle.
    ///
    /// Splitting it into three tells the truth and is more useful besides:
    ///
    /// <list type="bullet">
    /// <item><b>Uniform</b> - passes whose mask covers every engraved pixel. They cut, but
    /// they deepen everything equally, so what they produce is a flat recess under the whole
    /// design rather than any part of the image. Real material, real time, no form. Whether
    /// you want that recess is a design question, not a defect.</item>
    /// <item><b>Relief</b> - passes where the mask is shrinking. This is where the image is
    /// actually made, and it is the only part that carries shape.</item>
    /// <item><b>Empty</b> - passes whose threshold is darker than anything in the map, so
    /// nothing is in the mask at all. Whether a slicer skips these or runs them empty is an
    /// implementation detail this program does not know.</item>
    /// </list>
    ///
    /// The three always sum to the pass count.
    /// </summary>
    public readonly record struct PassBreakdown(int Uniform, int Relief, int Empty)
    {
        public int Total => Uniform + Relief + Empty;
    }

    /// <summary>
    /// Black is deepest, so a pixel at value v is engraved on the first
    /// round((1 - v/max) * passes) passes of the job. The darkest and lightest levels present
    /// therefore bracket where the relief lives, and everything outside that bracket is either
    /// uniform cutting or an empty mask.
    /// </summary>
    public PassBreakdown PassesAt(int passes)
    {
        if (passes <= 0 || UsedLevels.Length == 0 || MaxValue <= 0)
            return new PassBreakdown(0, 0, Math.Max(0, passes));

        int deepest = (int)Math.Round((1.0 - (double)MinLevel / MaxValue) * passes);
        int lightest = (int)Math.Round((1.0 - (double)MaxLevel / MaxValue) * passes);

        deepest = Math.Clamp(deepest, 0, passes);
        lightest = Math.Clamp(lightest, 0, deepest);

        return new PassBreakdown(lightest, deepest - lightest, passes - deepest);
    }

    /// <summary>
    /// How evenly the file's precision divides into engraved bands, at a given pass count.
    /// Null when the map has fewer levels than passes, because then the limit is how many
    /// depths exist at all, which <see cref="SlicesAt"/> already reports.
    ///
    /// This is a second, independent cost of low precision, and it is not the same question as
    /// how many depths you get. A slicer cuts the level range into equal bands; the levels are
    /// integers, so the bands cannot all hold the same number of them unless the pass count
    /// divides the range exactly. With 256 levels and 200 passes, 144 bands hold one level and
    /// 56 hold two - so on a smooth gradient the terraces come out in an irregular 1:2 pattern,
    /// even though all 200 depths are present and every pass removes the same material. With
    /// 65,536 levels and the same 200 passes the bands hold 327 or 328, a spread of 0.3%.
    ///
    /// Across every pass count from 2 to 256, only eight leave 8-bit precision evenly divided -
    /// the powers of two - while half of them produce a 2:1 spread. Nothing in that range
    /// troubles a genuine 16-bit map at all.
    ///
    /// Raised by Nathaniel Klumb on the LightBurn forum, against the reading that 16 bits only
    /// matter beyond 256 passes. His arithmetic reproduces exactly, and it is measured here
    /// against the ladder the file actually carries rather than the container it declares -
    /// which is the point for this program, because an imposter inherits the 8-bit behaviour
    /// while looking 16-bit in every file dialog.
    /// </summary>
    public (int Min, int Max, double Ratio)? BandSpreadAt(int passes)
    {
        if (passes <= 1 || UsedLevels.Length == 0) return null;

        // The rungs of the ladder, not the histogram: this is a question about the precision
        // the file carries, not about how the artwork happens to distribute across it.
        int step = Math.Max(1, LevelStep);
        int span = MaxLevel - MinLevel + 1;
        int rungs = (span + step - 1) / step;
        if (rungs < passes) return null;

        var counts = new int[passes];
        for (int k = 0; k < rungs; k++)
        {
            long v = (long)k * step;
            int q = (int)Math.Min(passes - 1, v * passes / span);
            counts[q]++;
        }

        int min = int.MaxValue, max = 0;
        foreach (int c in counts)
        {
            if (c == 0) continue;
            if (c < min) min = c;
            if (c > max) max = c;
        }

        if (min == int.MaxValue) return null;
        return (min, max, (double)max / min);
    }

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
