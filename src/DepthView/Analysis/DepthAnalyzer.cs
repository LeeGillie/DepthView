using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DepthView.Imaging;

namespace DepthView.Analysis;

public static class DepthAnalyzer
{
    /// <summary>Distinct-colour counting stops here to keep memory bounded on photographs.</summary>
    private const int ColorCap = 1_000_000;

    /// <summary>
    /// Distinct grey levels above which "the container is not full" stops being a criticism.
    ///
    /// Occupancy on its own is the wrong yardstick. A 16-bit container holds 65,536 levels,
    /// and essentially no real depth map fills it - so measuring a file against the container
    /// means warning about almost every genuine 16-bit map ever made. What matters is whether
    /// the file carries more gradation than the job can use, and a slicing job only ever
    /// resolves as many depth steps as it has passes. WeCreat's published figure of 256 depth
    /// layers for the Lumos Ultra is, per their support team, an 8-bit software representation
    /// rather than a controller limit - which makes it a statement about what the toolchain
    /// carries, not what the machine can do, and so exactly the ceiling that matters here.
    /// LightBurn pass counts run from tens to low hundreds, and the depth budget in TODO item
    /// 1.2 works out around 110 usable steps for a 1.1mm brass pocket at 10um.
    ///
    /// 1,024 is four times the highest of those figures, so a file clearing it has margin
    /// even against a process far finer than anything on the market. Below it, a thin map in
    /// a large container is worth flagging.
    ///
    /// This threshold never applies on its own: the level step must also be 1. An evenly
    /// spaced ladder is a quantisation signature no matter how many rungs it has, and that
    /// is what keeps samples/04-quantised-1024.png (1,014 levels, step 64) a warning while
    /// a genuine 7,814-level map is not.
    /// </summary>
    private const int AmpleLevels = 1024;

    public static AnalysisResult Analyze(ImageData img, ImageMetadata meta)
    {
        var sw = Stopwatch.StartNew();

        var r = new AnalysisResult
        {
            Meta = meta,
            Width = img.Width,
            Height = img.Height,
            Channels = img.Channels,
            BitDepth = img.BitDepth,
            PixelCount = img.PixelCount,
            IsFloat = img.Kind == SampleKind.Float
        };

        if (r.IsFloat) AnalyzeFloat(img, r);
        else AnalyzeInteger(img, r);

        ComputeStatistics(r);
        ComputeLevelStructure(r);
        Classify(r);

        r.Elapsed = sw.Elapsed;
        return r;
    }

    // ------------------------------------------------------------------ integer

    private static void AnalyzeInteger(ImageData img, AnalysisResult r)
    {
        int max = img.MaxValue;
        r.MaxValue = max;

        var s = img.Samples!;
        int ch = img.Channels;
        long n = img.PixelCount;

        var grey = new long[max + 1];
        r.GreyHistogram = grey;

        int aMin = int.MaxValue, aMax = int.MinValue;

        if (ch <= 2)
        {
            for (long i = 0; i < n; i++)
            {
                long o = i * ch;
                grey[s[o]]++;
                if (ch == 2)
                {
                    int a = s[o + 1];
                    if (a < aMin) aMin = a;
                    if (a > aMax) aMax = a;
                }
            }
            r.GreyPixels = n;
            r.NonGreyPixels = 0;
            r.UniqueNonGreyColors = 0;
            r.HasAlphaChannel = ch == 2;
        }
        else
        {
            var hr = new long[max + 1];
            var hg = new long[max + 1];
            var hb = new long[max + 1];
            r.HistR = hr; r.HistG = hg; r.HistB = hb;

            var nonGrey = new HashSet<ulong>();
            var allColors = new HashSet<ulong>();
            bool ngCapped = false, allCapped = false;
            long greyPx = 0, nonGreyPx = 0;

            for (long i = 0; i < n; i++)
            {
                long o = i * ch;
                int cr = s[o], cg = s[o + 1], cb = s[o + 2];

                hr[cr]++; hg[cg]++; hb[cb]++;

                if (cr == cg && cg == cb)
                {
                    grey[cr]++;
                    greyPx++;
                }
                else
                {
                    nonGreyPx++;
                    if (!ngCapped)
                    {
                        nonGrey.Add(Key(cr, cg, cb));
                        if (nonGrey.Count >= ColorCap) { ngCapped = true; }
                    }
                }

                if (!allCapped)
                {
                    allColors.Add(Key(cr, cg, cb));
                    if (allColors.Count >= ColorCap) { allCapped = true; }
                }

                if (ch == 4)
                {
                    int a = s[o + 3];
                    if (a < aMin) aMin = a;
                    if (a > aMax) aMax = a;
                }
            }

            r.GreyPixels = greyPx;
            r.NonGreyPixels = nonGreyPx;
            r.UniqueNonGreyColors = nonGrey.Count;
            r.NonGreyColorsCapped = ngCapped;
            r.UniqueColorsTotal = allColors.Count;
            r.TotalColorsCapped = allCapped;
            r.IsGrayscaleStoredAsColor = nonGreyPx == 0;
            r.HasAlphaChannel = ch == 4;

            r.UniqueR = CountNonZero(hr);
            r.UniqueG = CountNonZero(hg);
            r.UniqueB = CountNonZero(hb);
        }

        if (r.HasAlphaChannel && aMin <= aMax)
        {
            r.AlphaMin = aMin;
            r.AlphaMax = aMax;
            r.AlphaConstant = aMin == aMax;
        }

        r.UniqueGreyLevels = CountNonZero(grey);
    }

    // ------------------------------------------------------------------ float

    private static void AnalyzeFloat(ImageData img, AnalysisResult r)
    {
        var f = img.Floats!;
        int ch = img.Channels;
        long n = img.PixelCount;

        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        long greyPx = 0, nonGreyPx = 0;
        var distinct = new HashSet<float>();
        bool capped = false;

        for (long i = 0; i < n; i++)
        {
            long o = i * ch;
            float v = f[o];
            if (ch >= 3)
            {
                if (v == f[o + 1] && v == f[o + 2]) greyPx++;
                else { nonGreyPx++; continue; }
            }
            else greyPx++;

            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            if (!capped)
            {
                distinct.Add(v);
                if (distinct.Count >= ColorCap) capped = true;
            }
        }

        if (float.IsInfinity(min)) { min = 0; max = 0; }

        r.FloatMin = min;
        r.FloatMax = max;
        r.GreyPixels = greyPx;
        r.NonGreyPixels = nonGreyPx;
        r.IsGrayscaleStoredAsColor = ch >= 3 && nonGreyPx == 0;
        r.HistogramIsBinned = true;
        r.MaxValue = 65535;

        var hist = new long[65536];
        double span = max - min;
        double scale = span > 0 ? 65535.0 / span : 0;

        for (long i = 0; i < n; i++)
        {
            float v = f[i * ch];
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            int bin = span > 0 ? (int)Math.Clamp((v - min) * scale, 0, 65535) : 0;
            hist[bin]++;
        }

        r.GreyHistogram = hist;
        r.UniqueGreyLevels = capped ? ColorCap : distinct.Count;
        r.UniqueNonGreyColors = 0;
        r.NonGreyColorsCapped = capped;
    }

    // ------------------------------------------------------------------ shared

    private static void ComputeStatistics(AnalysisResult r)
    {
        var h = r.GreyHistogram;
        long total = 0;
        double sum = 0;
        int min = -1, max = -1;

        for (int i = 0; i < h.Length; i++)
        {
            if (h[i] == 0) continue;
            if (min < 0) min = i;
            max = i;
            total += h[i];
            sum += (double)i * h[i];
        }

        if (total == 0) return;

        r.MinLevel = min;
        r.MaxLevel = max;
        r.Mean = sum / total;

        // Endpoints. Pure white gets zero laser passes and pure black gets every pass,
        // so both counts matter more than any other single level, and when a file never
        // reaches an endpoint it is worth naming how close it got and with how many pixels.
        r.PureBlackPixels = h[0];
        r.PureWhitePixels = h[^1];
        r.DarkestLevel = min;
        r.DarkestCount = h[min];
        r.LightestLevel = max;
        r.LightestCount = h[max];
        r.HeadroomTop = (h.Length - 1) - max;
        r.HeadroomBottom = min;

        double varSum = 0;
        long cum = 0;
        long half = total / 2, p1t = (long)(total * 0.01), p99t = (long)(total * 0.99);
        bool gotMedian = false, gotP1 = false, gotP99 = false;

        for (int i = min; i <= max; i++)
        {
            long c = h[i];
            if (c == 0) continue;
            double d = i - r.Mean;
            varSum += d * d * c;
            cum += c;
            if (!gotP1 && cum >= p1t) { r.P1 = i; gotP1 = true; }
            if (!gotMedian && cum >= half) { r.Median = i; gotMedian = true; }
            if (!gotP99 && cum >= p99t) { r.P99 = i; gotP99 = true; }
        }

        r.StdDev = Math.Sqrt(varSum / total);

        r.TopLevels = Enumerable.Range(min, max - min + 1)
            .Where(i => h[i] > 0)
            .OrderByDescending(i => h[i])
            .Take(8)
            .Select(i => (i, h[i]))
            .ToList();
    }

    private static void ComputeLevelStructure(AnalysisResult r)
    {
        var h = r.GreyHistogram;
        var used = new List<int>(Math.Min(h.Length, 70000));
        for (int i = 0; i < h.Length; i++)
            if (h[i] > 0) used.Add(i);

        r.UsedLevels = used.ToArray();
        int unique = used.Count;

        r.Occupancy = h.Length > 0 ? (double)unique / h.Length : 0;
        r.RangeUtilisation = h.Length > 0 && unique > 0
            ? (double)(r.MaxLevel - r.MinLevel + 1) / h.Length
            : 0;
        r.EffectiveBits = unique <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(unique));

        if (unique < 2) { r.LevelStep = 1; return; }

        int gcd = 0, gaps = 0, largest = 0;
        for (int k = 1; k < used.Count; k++)
        {
            int d = used[k] - used[k - 1];
            gcd = Gcd(gcd, d);
            if (d > 1) { gaps++; if (d - 1 > largest) largest = d - 1; }
        }

        r.LevelStep = Math.Max(1, gcd);
        r.GapCount = gaps;
        r.LargestGap = largest;
        r.UniformLadder = gcd > 1 && (used[^1] - used[0]) == (long)gcd * (unique - 1);
    }

    // ------------------------------------------------------------------ verdict

    private static void Classify(AnalysisResult r)
    {
        var f = r.Findings;
        var used = r.UsedLevels;
        int unique = r.UniqueGreyLevels;
        int container = r.MaxValue + 1;

        if (r.IsFloat)
        {
            r.Verdict = "Floating point depth map";
            r.VerdictDetail =
                $"Range {r.FloatMin:G6} to {r.FloatMax:G6} with {(r.NonGreyColorsCapped ? "over " : "")}" +
                $"{unique:N0} distinct float values. Float maps have no quantisation ladder to test.";
            r.VerdictSeverity = Severity.Good;
            f.Add(new Finding(Severity.Info, "Continuous samples",
                "The histogram below is binned into 65,536 buckets across the value range; " +
                "bucket counts are exact but a bucket may cover many float values."));
            AddCommonFindings(r);
            return;
        }

        bool sixteen = r.BitDepth >= 16;
        bool replicated = unique > 0 && used.All(l => (l & 0xFF) == ((l >> 8) & 0xFF));
        bool highByteOnly = unique > 0 && used.All(l => (l & 0xFF) == 0);

        if (sixteen && unique > 0 && unique <= 256 && replicated)
        {
            r.Imposter = ImposterKind.Replicated257;
            r.Verdict = "IMPOSTER: 8-bit data in a 16-bit container";
            r.VerdictDetail =
                $"All {unique} distinct levels satisfy value = v x 257 (for example level {used[^1]:N0} " +
                $"= 0x{used[^1]:X4}, the same byte twice). " +
                "This is exactly what an 8-bit depth map looks like after being saved as 16-bit. " +
                "There is no additional precision in this file.";
            r.VerdictSeverity = Severity.Alert;
            f.Add(new Finding(Severity.Alert, "Byte-replicated levels",
                "Every 16-bit sample has its high byte equal to its low byte, so the file carries " +
                "at most 8 bits of real depth information while costing twice the storage."));
        }
        else if (sixteen && unique > 0 && unique <= 256 && highByteOnly)
        {
            r.Imposter = ImposterKind.HighByteOnly;
            r.Verdict = "IMPOSTER: 8-bit data shifted into 16 bits";
            r.VerdictDetail =
                $"All {unique} distinct levels have a zero low byte (value = v x 256). " +
                "An 8-bit map was left-shifted into a 16-bit container; the low byte is unused.";
            r.VerdictSeverity = Severity.Alert;
            f.Add(new Finding(Severity.Alert, "Low byte always zero",
                "The bottom 8 bits of every sample are zero. Real 16-bit depth data would populate them."));
        }
        else if (r.UniformLadder && r.LevelStep > 1)
        {
            r.Imposter = ImposterKind.QuantisedLadder;
            int sourceBits = (int)Math.Ceiling(Math.Log2(Math.Max(2, unique)));
            r.Verdict = $"IMPOSTER: {sourceBits}-bit data in a {r.BitDepth}-bit container";
            r.VerdictDetail =
                $"The {unique:N0} levels form a perfectly even ladder with a step of {r.LevelStep}. " +
                $"That is the signature of {sourceBits}-bit source data stretched across the " +
                $"{r.BitDepth}-bit range, not of native {r.BitDepth}-bit capture.";
            r.VerdictSeverity = Severity.Alert;
            f.Add(new Finding(Severity.Alert, "Uniform level ladder",
                $"Every used level sits on a multiple of {r.LevelStep} starting at {r.MinLevel}. " +
                "Genuine depth data almost never quantises this cleanly."));
        }
        else if (sixteen && unique > 0 && unique <= 256)
        {
            r.Imposter = ImposterKind.SparseLevels;
            r.Verdict = "Suspect: 16-bit container, 8-bit worth of levels";
            r.VerdictDetail =
                $"Only {unique} distinct grey levels across a 65,536 level container " +
                $"({r.Occupancy * 100:F3}% occupancy), but they do not follow a clean x257 or x256 pattern. " +
                "Likely 8-bit source data that was rescaled or filtered on the way in.";
            r.VerdictSeverity = Severity.Warn;
        }
        else if (unique > 0 && r.EffectiveBits < r.BitDepth - 1 && unique < container / 4
                 && unique >= AmpleLevels && r.LevelStep <= 1)
        {
            // Plenty of gradation, evenly spread, no quantisation signature. The container
            // is not full, and that is not a fault: see AmpleLevels for why.
            r.Verdict = $"Genuine {r.BitDepth}-bit data, carrying about {r.EffectiveBits} bits";
            r.VerdictDetail =
                $"{unique:N0} distinct grey levels in a {r.BitDepth}-bit container " +
                $"({r.Occupancy * 100:F2}% occupancy), spanning {r.MinLevel:N0} to {r.MaxLevel:N0}, " +
                $"with a level step of 1 and no byte-replication or uniform-ladder signature. " +
                $"The container could hold more, but no engraving pass count will consume this much.";
            r.VerdictSeverity = Severity.Good;

            f.Add(new Finding(Severity.Info, "Levels do not fill the container",
                $"{unique:N0} of a possible {container:N0} levels are used ({r.Occupancy * 100:F2}%). " +
                "That is worth knowing but is not a defect: the levels are evenly spread with a step " +
                "of 1, which is what genuine capture looks like, and a slicing job only ever resolves " +
                "as many depth steps as it has passes. This becomes a real constraint only if you " +
                "need more distinct depths than there are levels here."));
        }
        else if (unique > 0 && r.EffectiveBits < r.BitDepth - 1 && unique < container / 4)
        {
            r.Imposter = ImposterKind.SparseLevels;
            r.Verdict = $"Sparse: about {r.EffectiveBits} bits of real detail";
            r.VerdictDetail =
                $"{unique:N0} distinct levels in a {r.BitDepth}-bit container " +
                $"({r.Occupancy * 100:F3}% occupancy). The file can hold more detail than it carries.";
            r.VerdictSeverity = Severity.Warn;
        }
        else if (unique == 0)
        {
            r.Verdict = "No greyscale pixels";
            r.VerdictDetail = "Not one pixel in this image has R = G = B, so there is no grey ramp to analyse.";
            r.VerdictSeverity = Severity.Alert;
        }
        else
        {
            r.Verdict = $"Consistent with genuine {r.BitDepth}-bit depth data";
            r.VerdictDetail =
                $"{unique:N0} distinct grey levels ({r.Occupancy * 100:F2}% of the {container:N0} level container), " +
                $"spanning {r.MinLevel:N0} to {r.MaxLevel:N0}. No byte-replication or uniform-ladder signature.";
            r.VerdictSeverity = Severity.Good;
        }

        // ---- supporting findings ----

        if (r.NonGreyPixels > 0)
        {
            double pct = 100.0 * r.NonGreyPixels / r.PixelCount;
            f.Add(new Finding(pct > 1 ? Severity.Warn : Severity.Info, "Non-greyscale pixels present",
                $"{r.NonGreyPixels:N0} pixels ({pct:F3}%) have R, G and B that are not all equal, across " +
                $"{(r.NonGreyColorsCapped ? "at least " : "")}{r.UniqueNonGreyColors:N0} distinct colours. " +
                "A depth map should normally be pure grey; colour usually means JPEG chroma damage, " +
                "a colourised preview, or an encoded (turbo/viridis) depth image."));
        }

        if (r.UsedLevels.Length > 1)
        {
            // 256 is the reference point because it is where 8-bit runs out, so it is the
            // pass count at which "would a 16-bit file have helped" gets its answer.
            var (at256, _) = r.SlicesAt(256);
            var p256 = r.PassesAt(256);
            int lost256 = r.SlicesLostToHeadroom(256);

            // Deliberately no longer a warning just because stretching would add depths.
            // Whether the relief should be deeper is the operator's call, not a defect in the
            // file, and saying otherwise was the substance of a fair correction on the
            // LightBurn forum. What is worth flagging is a design sitting in a deep pocket it
            // may not have asked for.
            bool deepPocket = p256.Uniform >= 32;

            f.Add(new Finding(deepPocket ? Severity.Warn : Severity.Info, "What 256 passes would do",
                $"This map resolves {at256:N0} distinct depths at 256 passes. Of those passes, "
              + $"{p256.Relief:N0} form the relief, {p256.Uniform:N0} have every engraved pixel in "
              + $"the mask, and {p256.Empty:N0} have nothing in the mask at all."
              + (p256.Uniform > 0
                  ? $" The {p256.Uniform:N0} uniform passes still cut - they remove real material - but"
                  + " they deepen the whole design equally, so what they leave is a flat recess"
                  + " under it rather than any part of the picture. That is worth knowing whether"
                  + " or not you asked for a recess."
                  : " Nothing is cut uniformly: the lightest part of the design reaches bare surface.")
              + (lost256 > 0
                  ? $" Spreading the occupied levels across the full range would give {r.SlicesAtRemapped(256):N0}"
                  + " depths instead, by making the relief deeper rather than by recovering anything"
                  + " - levels per unit of depth do not change. Worth doing if the narrow range was"
                  + " an accident, and worth leaving alone if it was not."
                  : " The range is already fully used.")));

            // The other half of what precision buys, and the half that is easy to miss because
            // the depth count can look perfectly healthy while this is bad.
            //
            // A slicer cuts the level range into equal bands. Levels are integers, so unless
            // the pass count divides the range exactly, some bands hold more levels than
            // others - and on a smooth gradient that lands as terraces of uneven width, even
            // though every pass removes the same material. 200 is the example rather than 256
            // precisely because it is not a power of two: with 256 levels only the powers of
            // two divide evenly, so most real pass counts leave an 8-bit map with some bands
            // twice as wide as their neighbours, while a genuine 16-bit map stays inside half
            // a percent everywhere in that range.
            //
            // Raised by Nathaniel Klumb on the LightBurn forum, and checked before it went in.
            if (r.BandSpreadAt(200) is { } band && band.Ratio >= 1.5)
                f.Add(new Finding(band.Ratio >= 2 ? Severity.Warn : Severity.Info,
                    "Uneven slice bands at awkward pass counts",
                    $"At 200 passes the levels in this file divide into bands of {band.Min:N0} to "
                  + $"{band.Max:N0} levels - a {band.Ratio:F2}x spread. Every pass still cuts the same "
                  + "depth, but on a smooth gradient the terraces come out at uneven widths in that "
                  + "ratio. It happens because a slicer splits the range evenly while levels are "
                  + "integers, so only pass counts that divide the range exactly come out clean. With "
                  + $"{r.UniqueGreyLevels:N0} levels to work with that is a short list; a map carrying "
                  + "the full 16 bits stays within a fraction of a percent at any pass count. Pick a "
                  + "pass count that divides the range, or use a map with finer gradation."));
        }

        if (r.IsGrayscaleStoredAsColor)
            f.Add(new Finding(Severity.Info, "Grey data stored as RGB",
                $"Every pixel is neutral, but the file stores {r.Channels} channels, so the same value is " +
                $"repeated {r.Channels} times per pixel. Re-saving as single-channel greyscale would carry " +
                $"the same information in about {1.0 / r.Channels * 100:F0}% of the data, losing nothing. " +
                "It matters beyond tidiness: LightBurn treats a 24-bit image as 8-bit, so an RGB wrapper " +
                "can also throw away depth precision that the file appears to have."));

        if (r.MaxLevel < r.MaxValue || r.MinLevel > 0)
        {
            double util = r.RangeUtilisation * 100;
            f.Add(new Finding(util < 60 ? Severity.Warn : Severity.Info, "Range utilisation",
                $"Values occupy {r.MinLevel:N0}..{r.MaxLevel:N0} of a possible 0..{r.MaxValue:N0} " +
                $"({util:F1}% of the range). Whether that matters depends on how deep you meant "
              + "the relief to be: spreading the levels across the full range makes it deeper "
              + "rather than finer."));
        }

        if (r.GapCount > 0 && !r.UniformLadder)
            f.Add(new Finding(Severity.Info, "Histogram gaps",
                $"{r.GapCount:N0} gaps inside the occupied range, the largest being {r.LargestGap:N0} " +
                "consecutive empty levels. Isolated gaps are normal; a regular comb is not."));

        if (r.Meta.SignificantBits is { Length: > 0 })
            f.Add(new Finding(Severity.Info, "sBIT chunk present",
                $"The PNG declares {string.Join(", ", r.Meta.SignificantBits)} significant bits per channel. " +
                "Compare that with the measured effective bits above."));

        AddCommonFindings(r);
    }

    private static void AddCommonFindings(AnalysisResult r)
    {
        var f = r.Findings;

        AddEndpointFindings(r);

        if (!r.Meta.BitDepthIsExact)
            f.Add(new Finding(Severity.Warn, "Bit depth not exact",
                "The decoder normalised this file's samples, so imposter detection may be misleading. " +
                "PNG, PGM/PPM and PFM are read bit-exact; other formats depend on the decoder."));

        if (r.HasAlphaChannel)
            f.Add(new Finding(r.AlphaConstant ? Severity.Info : Severity.Warn, "Alpha channel",
                r.AlphaConstant
                    ? $"Alpha is constant at {r.AlphaMin:N0}, so it carries no information and could be dropped."
                    : $"Alpha varies from {r.AlphaMin:N0} to {r.AlphaMax:N0}. Some depth pipelines hide a " +
                      "confidence or validity mask here."));

        if (r.Meta.Gamma is { } g)
            f.Add(new Finding(Math.Abs(g - 1.0) < 0.001 ? Severity.Info : Severity.Warn, "Gamma declared",
                $"The file declares gamma {g:F5}. Depth maps should normally be linear (gamma 1.0); " +
                "a display gamma here means a viewer will distort the depth ramp."));

        if (r.Meta.HasIccProfile)
            f.Add(new Finding(Severity.Warn, "ICC colour profile embedded",
                "Colour management applied to a depth map will alter its values on the way to a viewer. " +
                "Depth maps are usually best stored with no profile."));

        if (r.Meta.Interlaced)
            f.Add(new Finding(Severity.Info, "Interlaced",
                "Adam7 interlacing is decoded correctly here, but it makes the file larger and slower to read."));

        foreach (var w in r.Meta.Warnings)
            f.Add(new Finding(Severity.Info, "Decoder note", w));
    }

    /// <summary>
    /// The two ends of the ramp deserve their own treatment. In a laser workflow pure white
    /// is the untouched surface and pure black is full depth, so their pixel counts say
    /// directly how much of the piece is left alone and how much is bottomed out. When a
    /// file never reaches an endpoint, naming how close it got is the useful answer.
    /// </summary>
    private static void AddEndpointFindings(AnalysisResult r)
    {
        var f = r.Findings;
        if (r.UniqueGreyLevels == 0) return;

        long total = r.GreyPixels > 0 ? r.GreyPixels : r.PixelCount;
        string what = r.IsFloat ? "the maximum value" : $"level {r.MaxValue:N0}";

        // --- light end ---
        if (r.PureWhitePixels > 0)
        {
            double pct = 100.0 * r.PureWhitePixels / total;
            f.Add(new Finding(pct > 1 ? Severity.Warn : Severity.Info, "Pure white present",
                $"{r.PureWhitePixels:N0} pixels ({pct:F3}%) sit exactly on {what}. " +
                "These are the untouched surface: zero laser passes, no material removed. " +
                (pct > 1
                    ? "A large flat white area can also mean the highlights were clipped, in which case " +
                      "detail above that point has already been thrown away."
                    : "A small count is normal for a map that just reaches its ceiling.")));
        }
        else
        {
            f.Add(new Finding(Severity.Info, "No pure white",
                $"Nothing in this image reaches {what}. The lightest level present is " +
                $"{r.LightestLevel:N0}, carried by {r.LightestCount:N0} pixels " +
                $"({100.0 * r.LightestCount / total:F3}%), leaving {r.HeadroomTop:N0} unused levels above it. " +
                "Every pixel will therefore be engraved to some degree, and nothing is left as bare surface."));
        }

        // --- dark end ---
        if (r.PureBlackPixels > 0)
        {
            double pct = 100.0 * r.PureBlackPixels / total;
            f.Add(new Finding(pct > 1 ? Severity.Warn : Severity.Info, "Pure black present",
                $"{r.PureBlackPixels:N0} pixels ({pct:F3}%) sit exactly on level 0, the deepest point. " +
                (pct > 1
                    ? "A large flat black area usually means the shadows were clipped: everything past that " +
                      "depth was flattened to the same value, so relief detail down there no longer exists."
                    : "A small count is normal for a map that just reaches its floor.")));
        }
        else
        {
            f.Add(new Finding(Severity.Info, "No pure black",
                $"Nothing in this image reaches level 0. The darkest level present is " +
                $"{r.DarkestLevel:N0}, carried by {r.DarkestCount:N0} pixels " +
                $"({100.0 * r.DarkestCount / total:F3}%), leaving {r.HeadroomBottom:N0} unused levels below it. " +
                "Rescaling the map to reach 0 would spend the full depth budget instead of part of it."));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ulong Key(int r, int g, int b)
        => ((ulong)(uint)r << 32) | ((ulong)(uint)g << 16) | (uint)b;

    private static int CountNonZero(long[] h)
    {
        int n = 0;
        for (int i = 0; i < h.Length; i++) if (h[i] != 0) n++;
        return n;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return Math.Abs(a);
    }
}
