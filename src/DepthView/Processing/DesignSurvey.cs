using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DepthView.Processing;

/// <summary>
/// The deepest or the highest part of a design.
///
/// <see cref="Found"/> means there is a real floor (or flat top) there: a measured, nearly level
/// area that holds a share of the design - or the background itself, when the user says the
/// background is part of the design. <see cref="Low"/> and <see cref="High"/> are its 1st and
/// 99th percentiles, so <see cref="Noise"/> is the roughness a level point can remove.
/// Without one, it is just the extreme percentile and there is no noise figure to show.
/// <see cref="Suggested"/> is the level point that makes it one exact level.
/// </summary>
public sealed record Extreme(bool Found, int Low, int High, long Pixels, double Share, int Suggested, string Source)
{
    /// <summary>Spread of the band, 1st to 99th percentile: the roughness on a flat floor or top.</summary>
    public int Noise => Found ? High - Low : 0;

    public static readonly Extreme None = new(false, 0, 0, 0, 0, 0, "none");
}

/// <summary>
/// Everything the tuning wizard measures about a map before it asks a single question.
///
/// Each question the wizard asks is about intent - is this background, should this be flat -
/// and each one is put with the measurement that makes it answerable. This class is those
/// measurements, and nothing here changes a pixel. It works on the source levels, before any
/// level points, so its answers do not move as the user experiments.
/// </summary>
public sealed class DesignSurvey
{
    public int Width, Height, MaxValue;

    /// <summary>The level around the image's edge, and how much of the whole image sits on it.</summary>
    public ushort Background;
    public long BackgroundPixels;
    public double BackgroundShare;

    /// <summary>Is the background below the middle of the design (a floor) or above it (a top)?</summary>
    public bool BackgroundIsLow;

    /// <summary>The background's own spread, 1st and 99th percentile of the pixels counted as background.</summary>
    public int BackgroundLow, BackgroundHigh;

    /// <summary>The design: its own centre and the distance to its furthest pixel, in source pixels.</summary>
    public bool HasDesign;
    public double CentreX, CentreY, Radius;

    /// <summary>
    /// Share of the area inside the design's circle that is at the background level. A picture
    /// of a coin on a surround has almost none - the background is all outside the coin. A
    /// design with a cut-away floor has a lot. This is the evidence for the wizard's first real
    /// question, and the reason it is a question: only the user knows which was meant.
    /// </summary>
    public double BackgroundInsideShare;
    public bool BackgroundLooksLikeFloor => BackgroundInsideShare >= 0.15;

    /// <summary>Distance from the design's centre to the canvas centre, in source pixels.</summary>
    public double OffCentrePx;

    /// <summary>The artwork's own raised rim, if it has one.</summary>
    public DepthCanvas.DesignRim? DrawnRim;

    /// <summary>
    /// Pixel noise in the design, in levels, by Immerkær's method: a whole-image estimate. Fine
    /// detail and steep edges in the design read as noise too, so on a clean map it reads high;
    /// the per-area <see cref="FlatArea.Jitter"/> is the figure that matters for flat areas.
    /// </summary>
    public double NoiseSigma;

    /// <summary>Nearly level areas inside the design (inside any drawn rim), largest first.</summary>
    public List<FlatArea> FlatAreas = new();

    /// <summary>
    /// Small flat tops below pure white that the slope under them does not explain -
    /// peaks clipped by whatever made the map (<see cref="Processing.FlatPeaks"/>). Largest first.
    /// </summary>
    public List<FlatPeak> FlatPeaks = new();

    public double Seconds;

    /// <summary>
    /// True when the surround is not one level - shaded, or marked with stray lines - as
    /// <see cref="DepthCanvas.SurroundMask"/> follows it in from the edge. Then the tuner needs
    /// <see cref="TuningOptions.UniformSurround"/> to see it the way this survey does.
    /// </summary>
    public bool SurroundShaded;

    /// <summary>Which source pixels are background: the surround, or the one background level.</summary>
    private bool[] _surround = Array.Empty<bool>();

    /// <summary>Is this source pixel part of the background?</summary>
    public bool IsSurround(int x, int y) => _surround.Length > 0 && _surround[(long)y * Width + x];

    // Histograms: every pixel, and the background's pixels; and the same two for the area
    // inside the design's drawn rim (or inside its edge), which is what is left when that rim
    // is replaced. A reading without the background subtracts the second from the first.
    private long[] _all = Array.Empty<long>();
    private long[] _bg = Array.Empty<long>();
    private long[] _inside = Array.Empty<long>();
    private long[] _insideBg = Array.Empty<long>();

    /// <summary>
    /// The design's floor, for one reading of it. <paramref name="backgroundIsDesign"/>: the user
    /// says the background is a cut-away floor, not a surround to be removed.
    /// <paramref name="drawnRimCovered"/>: the artwork's own rim is being replaced by ours, so
    /// only what is inside it counts (its bevel is often the deepest thing in the picture).
    /// </summary>
    public Extreme Floor(bool backgroundIsDesign, bool drawnRimCovered = false)
        => Extremity(deepest: true, backgroundIsDesign, drawnRimCovered);

    /// <summary>The design's top, read the same way as <see cref="Floor"/>.</summary>
    public Extreme Top(bool backgroundIsDesign, bool drawnRimCovered = false)
        => Extremity(deepest: false, backgroundIsDesign, drawnRimCovered);

    /// <summary>
    /// The histogram of the design under one reading: with or without the background, and
    /// with or without what lies under a drawn rim that is being replaced.
    /// </summary>
    private long[] Reading(bool bgIsDesign, bool rimCovered)
    {
        bool inside = rimCovered && DrawnRim is not null;
        var hist = (long[])(inside ? _inside : _all).Clone();
        if (!bgIsDesign)
        {
            var bg = inside ? _insideBg : _bg;
            for (int v = 0; v < hist.Length; v++) hist[v] -= bg[v];
        }
        return hist;
    }

    /// <summary>Background pixels within a reading, by level.</summary>
    private long[] BackgroundHist(bool rimCovered) => rimCovered && DrawnRim is not null ? _insideBg : _bg;

    private static long Sum(long[] hist)
    {
        long n = 0;
        foreach (long c in hist) n += c;
        return n;
    }

    /// <summary>A copy of the design's histogram under one reading, for drawing.</summary>
    public long[] ReadingHistogram(bool backgroundIsDesign, bool drawnRimCovered)
        => Reading(backgroundIsDesign, drawnRimCovered);

    /// <summary>The level at quantile <paramref name="q"/> of the design under one reading.</summary>
    public int PercentileLevel(double q, bool backgroundIsDesign, bool drawnRimCovered)
        => Percentile(Reading(backgroundIsDesign, drawnRimCovered), q);

    /// <summary>Pixels of the design, under one reading, between two levels - and the design's total.</summary>
    public (long Count, long Total) CountIn(int low, int high, bool backgroundIsDesign, bool drawnRimCovered)
    {
        var hist = Reading(backgroundIsDesign, drawnRimCovered);
        long n = 0, total = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            total += hist[v];
            if (v >= low && v <= high) n += hist[v];
        }
        return (n, total);
    }

    private Extreme Extremity(bool deepest, bool bgIsDesign, bool rimCovered)
    {
        var hist = Reading(bgIsDesign, rimCovered);

        long total = 0;
        int min = -1, max = -1;
        for (int v = 0; v < hist.Length; v++)
        {
            if (hist[v] == 0) continue;
            total += hist[v];
            if (min < 0) min = v;
            max = v;
        }
        if (total == 0) return Extreme.None;
        double span = Math.Max(1, max - min);

        // 1. The background, when it is part of the design and sits at this end of it.
        if (bgIsDesign && BackgroundPixels > 0 && BackgroundIsLow == deepest)
        {
            bool atEnd = deepest ? BackgroundLow <= min + span * 0.03 : BackgroundHigh >= max - span * 0.03;
            long bgCount = Sum(BackgroundHist(rimCovered));
            if (atEnd && bgCount >= total * 0.02)
            {
                return new Extreme(true, BackgroundLow, BackgroundHigh, bgCount, bgCount / (double)total,
                                   deepest ? BackgroundHigh : BackgroundLow, "background");
            }
        }

        // 2. A measured flat area at this end, holding a real share of the design.
        foreach (var a in FlatAreas)
        {
            if (a.ShareOfDesign < 0.02) continue;
            if (deepest ? !a.TouchesFloor : !a.TouchesTop) continue;
            // Must be at this end of the whole reading, not just of the inside: a kept drawn
            // rim stands above everything inside it. A thin anti-aliased edge or bevel beyond
            // it is allowed - up to 5% of the reading - and joins the floor (or top) when the
            // level point is set there, which is what an edge that soft would do anyway.
            long beyond = 0;
            if (deepest) for (int v = 0; v < a.Low; v++) beyond += hist[v];
            else for (int v = a.High + 1; v < hist.Length; v++) beyond += hist[v];
            if (beyond > total * 0.05) continue;
            int pad = (int)Math.Ceiling(a.Jitter * 2);
            int suggested = deepest ? Math.Min(MaxValue, a.High + pad) : Math.Max(0, a.Low - pad);
            return new Extreme(true, a.Low, a.High, a.Pixels, a.ShareOfDesign, suggested, $"flat area {a.Rank}");
        }

        // 3. A detached tail: a few pixels at the very end, standing apart from the rest of the
        // design across an empty stretch of levels. Those pockets are already the deepest (or
        // highest) thing in the design; a level point at the far side of the gap keeps them
        // so, and gives the empty stretch - layers that would cut nothing new - to the relief.
        if (Gap(hist, total, deepest) is { } g)
            return new Extreme(false, g.Low, g.High, g.Pixels, g.Pixels / (double)total,
                               deepest ? g.High : g.Low, "gap");

        // 4. No floor or top: the extreme tenth of a percent, as Suggest has always used.
        int pick = Percentile(hist, deepest ? 0.001 : 0.999);
        return new Extreme(false, pick, pick, 0, 0, pick, "percentile");
    }

    /// <summary>
    /// An empty stretch of at least 2% of the range, with no more than 1% of the design beyond
    /// it, at one end of a reading. <c>Low</c>..<c>High</c> are the empty levels and
    /// <c>Pixels</c> the pixels beyond them. The innermost such stretch wins, so the level
    /// point lands where the body of the design begins.
    /// </summary>
    private (int Low, int High, long Pixels)? Gap(long[] hist, long total, bool deepest)
    {
        int minGap = Math.Max(2, (int)(MaxValue * 0.02));
        long limit = (long)(total * 0.01);
        (int, int, long)? found = null;
        long beyond = 0;
        int runStart = -1;
        for (int k = 0; k < hist.Length; k++)
        {
            int v = deepest ? k : hist.Length - 1 - k;
            if (hist[v] == 0)
            {
                if (runStart < 0 && beyond > 0) runStart = v;
                continue;
            }
            if (runStart >= 0)
            {
                int len = Math.Abs(v - runStart);
                if (len >= minGap && beyond <= limit)
                    found = deepest ? (runStart, v - 1, beyond) : (v + 1, runStart, beyond);
                runStart = -1;
            }
            beyond += hist[v];
            if (beyond > limit) break;
        }
        return found;
    }

    /// <summary>The level at quantile <paramref name="q"/> of a histogram.</summary>
    public static int Percentile(long[] hist, double q)
    {
        long total = 0;
        foreach (long c in hist) total += c;
        if (total == 0) return 0;
        long target = Math.Max(1, (long)Math.Ceiling(total * q));
        long run = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            run += hist[v];
            if (run >= target) return v;
        }
        return hist.Length - 1;
    }


    public static DesignSurvey Run(ushort[] grey, int w, int h, int maxValue)
    {
        var sw = Stopwatch.StartNew();
        var s = new DesignSurvey { Width = w, Height = h, MaxValue = maxValue };
        int tol = DepthCanvas.ContentTolerance(maxValue);
        s.Background = DepthCanvas.BackgroundLevel(grey, w, h);

        // The background. On a clean map it is every pixel at the one level around the edge,
        // which also catches a cut-away floor wherever it shows through the design. On a map
        // whose surround is shaded - a rendered coin on a vignetted backdrop - that test calls
        // most of the surround design, so the surround is followed from the edge instead.
        // Both, when there is a surround to follow: the flood catches shading and stray marks
        // joined to the edge (faint grid lines left in the corners of an exported image), the
        // level test catches a floor showing through the design that the flood cannot reach.
        var bg = DepthCanvas.SurroundMask(grey, w, h, maxValue) ?? new bool[grey.Length];
        long offLevel = 0;
        for (long i = 0; i < grey.Length; i++)
        {
            bool atLevel = Math.Abs(grey[i] - s.Background) <= tol;
            if (bg[i] && !atLevel) offLevel++;
            bg[i] |= atLevel;
        }
        // Uneven enough to fool a one-level test: the tuner then needs it evened out too.
        // A fifth of a percent of the image: below that it is the soft last pixels of the
        // coin's own edge, which the flood brushes against, not a surround that needs evening.
        s.SurroundShaded = offLevel > Math.Max(500, grey.Length / 500);
        s._surround = bg;

        s._all = new long[maxValue + 1];
        s._bg = new long[maxValue + 1];
        var noBg = new long[maxValue + 1];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            long row = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                int v = grey[row + x];
                s._all[v]++;
                if (bg[row + x]) { s._bg[v]++; s.BackgroundPixels++; continue; }
                noBg[v]++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        s.BackgroundShare = s.BackgroundPixels / (double)grey.Length;
        s.BackgroundLow = Percentile(s._bg, 0.01);
        s.BackgroundHigh = Percentile(s._bg, 0.99);
        s.BackgroundIsLow = s.Background <= Percentile(noBg, 0.5);
        s._inside = s._all;
        s._insideBg = s._bg;

        if (maxX >= 0)
        {
            // The middle of the box round the design, as DepthCanvas.DesignCentre does, and
            // the distance to its furthest pixel.
            s.HasDesign = true;
            double cx = (minX + maxX) / 2.0, cy = (minY + maxY) / 2.0;
            s.CentreX = cx;
            s.CentreY = cy;
            double far2 = 0;
            for (int y = minY; y <= maxY; y++)
            {
                long row = (long)y * w;
                double dy2 = Sq(y - cy);
                for (int x = minX; x <= maxX; x++)
                    if (!bg[row + x]) far2 = Math.Max(far2, Sq(x - cx) + dy2);
            }
            s.Radius = Math.Sqrt(far2);
            s.OffCentrePx = Math.Sqrt(Sq(cx - (w - 1) / 2.0) + Sq(cy - (h - 1) / 2.0));
            s.DrawnRim = DepthCanvas.DetectDesignRim(grey, w, h, maxValue, s.Background, cx, cy, s.Radius, bg);

            // Background inside the design's circle, kept a little inside its edge so the
            // anti-aliased rim of a coin does not count as floor; and the histograms of what is
            // inside any drawn rim.
            double r2 = Sq(s.Radius * 0.95);
            double rIn = s.DrawnRim?.Inner ?? s.Radius;
            double in2 = Sq(rIn * 0.98);
            double scan2 = Math.Max(r2, in2);
            s._inside = new long[maxValue + 1];
            s._insideBg = new long[maxValue + 1];
            long inside = 0, insideBg = 0;
            for (int y = 0; y < h; y++)
            {
                double dy2 = Sq(y - cy);
                if (dy2 > scan2) continue;
                long row = (long)y * w;
                for (int x = 0; x < w; x++)
                {
                    double d2 = Sq(x - cx) + dy2;
                    int v = grey[row + x];
                    bool isBg = bg[row + x];
                    if (d2 <= in2) { s._inside[v]++; if (isBg) s._insideBg[v]++; }
                    if (d2 > r2) continue;
                    inside++;
                    if (isBg) insideBg++;
                }
            }
            s.BackgroundInsideShare = inside > 0 ? insideBg / (double)inside : 0;

            s.FlatAreas = Processing.FlatAreas.Find(grey, w, h, maxValue, (x, y) =>
                Sq(x - cx) + Sq(y - cy) < in2 && !bg[(long)y * w + x]);
            s.FlatPeaks = Processing.FlatPeaks.Find(grey, w, h, maxValue, (x, y) =>
                Sq(x - cx) + Sq(y - cy) < in2 && !bg[(long)y * w + x]);
        }

        s.NoiseSigma = Immerkaer(grey, w, h, bg);
        s.Seconds = sw.Elapsed.TotalSeconds;
        return s;
    }

    /// <summary>
    /// Immerkaer's fast noise estimate: one Laplacian-difference convolution, summed in absolute
    /// value over the design (pixels whose 3 x 3 neighbourhood is clear of the background).
    /// </summary>
    private static double Immerkaer(ushort[] p, int w, int h, bool[] bg)
    {
        double sum = 0;
        long n = 0;
        for (int y = 1; y < h - 1; y++)
        {
            long r0 = (long)(y - 1) * w, r1 = (long)y * w, r2 = (long)(y + 1) * w;
            for (int x = 1; x < w - 1; x++)
            {
                if (bg[r1 + x] || bg[r0 + x - 1] || bg[r0 + x + 1] || bg[r2 + x - 1] || bg[r2 + x + 1]) continue;
                int c = p[r1 + x];
                int a = p[r0 + x - 1], b = p[r0 + x], d = p[r0 + x + 1];
                int e = p[r1 + x - 1], f = p[r1 + x + 1];
                int g = p[r2 + x - 1], i = p[r2 + x], j = p[r2 + x + 1];
                double conv = a - 2 * b + d - 2 * e + 4 * c - 2 * f + g - 2 * i + j;
                sum += Math.Abs(conv);
                n++;
            }
        }
        return n == 0 ? 0 : Math.Sqrt(Math.PI / 2) * sum / (6.0 * n);
    }

    private static double Sq(double v) => v * v;
}
