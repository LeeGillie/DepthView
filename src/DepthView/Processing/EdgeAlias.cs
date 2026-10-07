using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>What <see cref="EdgeAlias.Measure"/> found: how many sharp edges are pixel stairs.</summary>
public sealed class AliasReport
{
    public int Width, Height;
    public double PixelsPerMm, BlankRadiusPx;
    /// <summary>A change at least this many levels, inside four pixels, counts as an edge.</summary>
    public double MinStepLevels;
    public long BlankPixels;
    /// <summary>Diagonal and curved step edges judged, and those that jump in one pixel.</summary>
    public long EdgePixels, Aliased;
    public double Seconds;
    /// <summary>Per pixel: 2 on an aliased edge. Only kept when asked for.</summary>
    public byte[]? Classes;

    public double Share => EdgePixels == 0 ? 0 : (double)Aliased / EdgePixels;

    /// <summary>Enough edges to judge, and most of them stairs.</summary>
    public bool Jagged => EdgePixels >= EdgeAlias.MinEdges && Share >= EdgeAlias.JaggedShare;
}

/// <summary>
/// Jagged edges (TODO 9.8). A map built at two or three times its final size and reduced
/// carries in-between levels along its edges, because each edge pixel averages the plateaus
/// either side; those come out smooth. A map rendered at its final size jumps from one plateau
/// to the next in a single pixel, and every diagonal and curve is cut as a staircase.
///
/// So: find the sharp step edges (most of the change across four pixels happens in two),
/// keep only the diagonal and curved ones - a wall along the pixel grid is a one-pixel jump
/// whatever made it, so it proves nothing - and count those whose change happens in one
/// pixel. On synthetic art rendered at its final size about 95% are; reduced from 3-4x,
/// 15-35%. Smooth slopes are not edges and are not counted. The outer 4% of the blank is left
/// out, so a rim (DepthView's own or the artwork's) does not decide the answer.
///
/// Inspection only: DepthView never resamples the map. The cure is to export larger and
/// reduce in the image editor.
/// </summary>
public static class EdgeAlias
{
    /// <summary>Share of stairs at which a map reads as jagged (E: from synthetic art).</summary>
    public const double JaggedShare = 0.6;

    /// <summary>Fewer judged edge pixels than this and there is nothing to say.</summary>
    public const long MinEdges = 200;

    /// <summary>One step carrying this much of the change across the window is a stair.</summary>
    private const double OneStep = 0.9;

    public static AliasReport Measure(ushort[] grey, int w, int h, int maxValue, double pixelsPerMm,
                                      bool keepClasses, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var r = new AliasReport { Width = w, Height = h, PixelsPerMm = pixelsPerMm };
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        r.BlankRadiusPx = Math.Min(w, h) / 2.0;
        double inner = r.BlankRadiusPx * 0.96, inner2 = inner * inner;

        // The range the design uses, and the size of one of its own level steps, so neither a
        // shallow map nor an 8-bit map saved as 16 bits (steps of 257) fools the threshold.
        var hist = new long[maxValue + 1];
        long n = 0;
        for (int y = 0; y < h; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy > inner2) continue;
                hist[grey[(long)y * w + x]]++;
                n++;
            }
        }
        r.BlankPixels = n;
        if (n == 0) return r;

        int lo = Percentile(hist, n, 0.001), hi = Percentile(hist, n, 0.999);
        int distinct = 0, prev = -1;
        long gapSum = 0;
        for (int v = lo; v <= hi; v++)
        {
            if (hist[v] == 0) continue;
            if (prev >= 0) gapSum += v - prev;
            prev = v;
            distinct++;
        }
        double quantum = distinct > 1 ? (double)gapSum / (distinct - 1) : 1;
        double range = Math.Max(1, hi - lo);
        // Capped: a map of a few flat levels has huge gaps between them, and its edges are
        // exactly what is being judged.
        double minStep = Math.Max(0.04 * range, Math.Min(3 * quantum, 0.25 * range));
        r.MinStepLevels = minStep;

        byte[]? cls = keepClasses ? new byte[grey.LongLength] : null;
        long edges = 0, aliased = 0;
        object gate = new();

        Parallel.For(3, h - 3, () => (0L, 0L), (y, _, acc) =>
        {
            if (ct.IsCancellationRequested) return acc;
            double dy0 = y - cy;
            long row = (long)y * w;
            for (int x = 3; x < w - 3; x++)
            {
                double dx0 = x - cx;
                if (dx0 * dx0 + dy0 * dy0 > inner2) continue;
                long i = row + x;
                double gx = (grey[i + 1] - grey[i - 1]) * 0.5;
                double gy = (grey[i + w] - grey[i - w]) * 0.5;
                double mag = Math.Sqrt(gx * gx + gy * gy);
                if (mag * 2 < minStep * 0.5) continue;

                // Diagonal and curved edges only.
                double ang = Math.Atan2(Math.Abs(gy), Math.Abs(gx)) * 180 / Math.PI;   // 0..90
                if (ang < 20 || ang > 70) continue;

                double ux = gx / mag, uy = gy / mag;
                int sx = (int)Math.Round(ux), sy = (int)Math.Round(uy);
                if (mag < Mag(grey, w, i + sy * w + sx) || mag < Mag(grey, w, i - sy * w - sx)) continue;

                // The profile across the edge, five samples along the gradient.
                double S(int t) => grey[(long)(int)Math.Round(y + t * uy) * w + (int)Math.Round(x + t * ux)];
                double s0 = S(-2), s1 = S(-1), s2 = S(0), s3 = S(1), s4 = S(2);
                double tot = s4 - s0;
                if (Math.Abs(tot) < minStep) continue;

                double big = 0, pair = 0, last = 0;
                bool monotonic = true;
                for (int k = 0; k < 4; k++)
                {
                    double d = k switch { 0 => s1 - s0, 1 => s2 - s1, 2 => s3 - s2, _ => s4 - s3 };
                    if (d * tot < 0 && Math.Abs(d) > 0.05 * Math.Abs(tot)) { monotonic = false; break; }
                    double ad = Math.Abs(d);
                    if (ad > big) big = ad;
                    if (k > 0 && ad + last > pair) pair = ad + last;
                    last = ad;
                }
                // A thin line or a ridge goes up and down; a ramp spreads its change out. Neither
                // is a step edge.
                if (!monotonic || pair < 0.85 * Math.Abs(tot)) continue;

                acc.Item1++;
                if (big >= OneStep * Math.Abs(tot))
                {
                    acc.Item2++;
                    if (cls is not null) cls[i] = 2;
                }
            }
            return acc;
        }, acc => { lock (gate) { edges += acc.Item1; aliased += acc.Item2; } });
        ct.ThrowIfCancellationRequested();

        r.EdgePixels = edges;
        r.Aliased = aliased;
        r.Classes = cls;
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    private static double Mag(ushort[] g, int w, long i)
    {
        double gx = (g[i + 1] - g[i - 1]) * 0.5, gy = (g[i + w] - g[i - w]) * 0.5;
        return Math.Sqrt(gx * gx + gy * gy);
    }

    private static int Percentile(long[] hist, long n, double p)
    {
        long target = (long)Math.Ceiling(n * p), seen = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            seen += hist[v];
            if (seen >= Math.Max(1, target)) return v;
        }
        return hist.Length - 1;
    }
}
