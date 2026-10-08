using System;
using System.Diagnostics;

namespace DepthView.Processing;

public enum DitherKind { None, Ordered, ErrorDiffused, Unknown }

/// <summary>What <see cref="Dither.Detect"/> found inside the blank.</summary>
public sealed class DitherReport
{
    /// <summary>Distinct levels inside the blank.</summary>
    public int Levels;
    /// <summary>Share of neighbouring pixel pairs (across and down) whose levels differ.</summary>
    public double TransitionShare;
    /// <summary>How periodic the fine pattern is (mean autocorrelation at lags 4 and 8); null
    /// when not measured.</summary>
    public double? Periodicity;
    public long BlankPixels;
    public DitherKind Kind;
    public double Seconds;

    public bool Dithered => Kind != DitherKind.None;

    public string KindName => Kind switch
    {
        DitherKind.Ordered => "ordered",
        DitherKind.ErrorDiffused => "error-diffused",
        DitherKind.Unknown => "dithered",
        _ => "none",
    };
}

/// <summary>
/// Dither detection (TODO 7.5). A dithered image encodes tone as the density of a few levels -
/// usually black and white dots - and a slicer reads that density as geometry: every dot is a
/// pit or a pin, and the picture's tones are lost. Every level statistic of such a file is
/// meaningless, so this is checked before anything else is said about it.
///
/// The signature: very few distinct levels (16 or fewer) with a large share of neighbouring
/// pixels differing - dots, not areas. Line art with few levels changes level only along its
/// lines, a few percent of pairs; a dithered mid-tone changes on a third to a half of them.
/// The kind comes from the fine pattern left after taking out the local mean: an ordered
/// (Bayer) dither repeats every 4 or 8 pixels, error diffusion does not.
/// </summary>
public static class Dither
{
    public const int MaxLevels = 16;
    public const double TransitionThreshold = 0.15;
    /// <summary>Periodicity at or above this reads as an ordered dither (E: from synthetic Bayer and Floyd-Steinberg).</summary>
    public const double OrderedThreshold = 0.25;

    public static DitherReport Detect(ushort[] grey, int w, int h, int maxValue)
    {
        var sw = Stopwatch.StartNew();
        var r = new DitherReport();
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double rad = Math.Min(w, h) / 2.0 - 1.5, rad2 = rad * rad;
        if (w < 16 || h < 16) return r;

        var seen = new bool[maxValue + 1];
        long n = 0, pairs = 0, diff = 0;
        for (int y = 0; y < h - 1; y++)
        {
            double dy = y - cy;
            long row = (long)y * w;
            for (int x = 0; x < w - 1; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy > rad2) continue;
                long i = row + x;
                int v = grey[i];
                if (!seen[v]) { seen[v] = true; r.Levels++; }
                n++;
                pairs += 2;
                if (grey[i + 1] != v) diff++;
                if (grey[i + w] != v) diff++;
            }
        }
        r.BlankPixels = n;
        r.TransitionShare = pairs == 0 ? 0 : (double)diff / pairs;
        if (r.Levels > MaxLevels || r.Levels < 2 || r.TransitionShare < TransitionThreshold)
        {
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        r.Periodicity = Periodicity(grey, w, h, maxValue);
        r.Kind = r.Periodicity is double p
            ? (p >= OrderedThreshold ? DitherKind.Ordered : DitherKind.ErrorDiffused)
            : DitherKind.Unknown;
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    /// <summary>
    /// Mean autocorrelation, across and down, at lags 4 and 8 of the image minus its 9 x 9 local
    /// mean, over the central square of the blank (at most 1024 px). The local mean takes out
    /// the picture's own tone, which would otherwise correlate at every lag.
    /// </summary>
    private static double? Periodicity(ushort[] grey, int w, int h, int maxValue)
    {
        int side = Math.Min(1024, (int)(Math.Min(w, h) / Math.Sqrt(2)) - 16);
        if (side < 64) return null;
        int x0 = (w - side) / 2, y0 = (h - side) / 2;
        const int R = 4;
        int n = side;
        // Integral image of the window, for the 9 x 9 means.
        var integ = new double[(n + 1) * (n + 1)];
        for (int y = 0; y < n; y++)
        {
            double run = 0;
            for (int x = 0; x < n; x++)
            {
                run += grey[(long)(y0 + y) * w + x0 + x] / (double)Math.Max(1, maxValue);
                integ[(y + 1) * (n + 1) + x + 1] = integ[y * (n + 1) + x + 1] + run;
            }
        }
        var hp = new double[n * n];
        double var0 = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int ya = Math.Max(0, y - R), yb = Math.Min(n, y + R + 1), xa = Math.Max(0, x - R), xb = Math.Min(n, x + R + 1);
                double s = integ[yb * (n + 1) + xb] - integ[ya * (n + 1) + xb] - integ[yb * (n + 1) + xa] + integ[ya * (n + 1) + xa];
                double m = s / ((yb - ya) * (xb - xa));
                double v = grey[(long)(y0 + y) * w + x0 + x] / (double)Math.Max(1, maxValue) - m;
                hp[y * n + x] = v;
                var0 += v * v;
            }
        if (var0 <= 1e-12) return null;

        double total = 0;
        int terms = 0;
        foreach (int lag in new[] { 4, 8 })
        {
            double ax = 0, ay = 0;
            for (int y = 0; y < n - lag; y++)
                for (int x = 0; x < n - lag; x++)
                {
                    double v = hp[y * n + x];
                    ax += v * hp[y * n + x + lag];
                    ay += v * hp[(y + lag) * n + x];
                }
            double norm = var0 * (n - lag) / (double)n;
            total += ax / norm + ay / norm;
            terms += 2;
        }
        return total / terms;
    }
}
