using System;
using System.Collections.Generic;
using System.Linq;

namespace DepthView.Processing;

/// <summary>What to do to an area that is meant to be flat.</summary>
public enum FlatMode
{
    /// <summary>Leave it exactly as drawn.</summary>
    Leave,

    /// <summary>
    /// Remove pixel-to-pixel jitter only: each pixel becomes the mean of its in-band neighbours.
    /// A gentle slope or dish survives; the speckle riding on it does not.
    /// </summary>
    Smooth,

    /// <summary>
    /// Make it one level: every in-band pixel becomes the area's median. Removes the jitter and
    /// any dish as well, so it changes the design, and the change is reported.
    /// </summary>
    Flatten,
}

/// <summary>
/// One flat-area change, stated on the source image so it means the same at any resolution:
/// the mask covers the whole image at its own size and is scaled onto whatever buffer the tuner
/// is working on - the dialog's preview or the full map. Only pixels inside the mask
/// <i>and</i> between <see cref="Low"/> and <see cref="High"/> are touched, so lettering or a
/// slope crossing the area keeps its own levels.
/// </summary>
public sealed record FlatAction(bool[] Mask, int MaskW, int MaskH, int Low, int High, int Level, FlatMode Mode);

/// <summary>
/// An area of the design that is nearly level: what the wizard offers to flatten or smooth.
/// Levels are source levels, before any level points.
/// </summary>
public sealed class FlatArea
{
    public int Rank;                // 1 = the largest
    public bool[] Mask = Array.Empty<bool>();
    public int MaskW, MaskH;
    public long Pixels;             // estimated, in source pixels
    public double ShareOfDesign;
    public int Median, Low, High;   // levels at the 50th, 1st and 99th percentiles
    public double Jitter;           // median pixel-to-pixel deviation, in levels
    public double CentreX, CentreY; // source pixels, for labelling
    public bool TouchesFloor;       // its median is within the design's deepest 3%
    public bool TouchesTop;         // its median is within the design's highest 3%

    /// <summary>Spread across the area, 1st to 99th percentile, in levels.</summary>
    public int Spread => High - Low;

    /// <summary>
    /// Mostly jitter, or mostly a real slope? Jitter alone spreads an area by a few times its
    /// own size; anything wider is the shape of the surface.
    /// </summary>
    public bool MostlyJitter => Spread <= Math.Max(4, Jitter * 6);

    /// <summary>The band a change may touch: the area's own spread, plus two jitters either side.</summary>
    public (int Low, int High) Band(int maxValue)
    {
        int pad = (int)Math.Ceiling(Jitter * 2) + 1;
        return (Math.Max(0, Low - pad), Math.Min(maxValue, High + pad));
    }

    public FlatAction ToAction(FlatMode mode, int maxValue)
    {
        var (lo, hi) = Band(maxValue);
        return new FlatAction(Mask, MaskW, MaskH, lo, hi, Median, mode);
    }

    /// <summary>
    /// How many slice boundaries the area straddles at a pass count, after the level points.
    /// Each one is a contour line - or, where it runs through jitter, a speckled patch - across
    /// something that was meant to be level.
    /// </summary>
    public int BoundariesCrossed(int passes, int black, int white, int maxValue)
    {
        if (passes < 2 || white <= black) return 0;
        double Map(int v) => Math.Clamp((v - black) / (double)(white - black), 0, 1);
        double band = 1.0 / (passes - 1);
        // Slices fall at the midpoints between the levels a slicer rounds to.
        int Slice(double t) => (int)Math.Floor(t / band + 0.5);
        return Math.Abs(Slice(Map(High)) - Slice(Map(Low)));
    }
}

public static class FlatAreas
{
    /// <summary>Areas this level or flatter count as flat: 0.05% of the range per source pixel.</summary>
    public const double SlopeLimit = 0.0005;

    /// <summary>
    /// Find the nearly level areas of the design, largest first.
    ///
    /// Works on a reduced copy (1,024 px on the long edge, nearest-neighbour so no level is
    /// invented) because it is a survey, not an edit: a light blur, the slope from central
    /// differences, and every connected run of pixels flatter than <see cref="SlopeLimit"/>.
    /// The changes it leads to are made at full resolution, with the mask scaled up and the
    /// level band deciding which pixels are touched.
    /// </summary>
    public static List<FlatArea> Find(ushort[] grey, int w, int h, int maxValue,
                                      Func<int, int, bool> inDesign, int maxAreas = 6)
    {
        double s = Math.Min(1.0, 1024.0 / Math.Max(w, h));
        int ww = Math.Max(1, (int)Math.Round(w * s)), wh = Math.Max(1, (int)Math.Round(h * s));
        var v = new int[ww * wh];
        var design = new bool[ww * wh];
        for (int y = 0; y < wh; y++)
        {
            int sy = (int)((long)y * h / wh);
            for (int x = 0; x < ww; x++)
            {
                int sx = (int)((long)x * w / ww);
                v[y * ww + x] = grey[(long)sy * w + sx];
                design[y * ww + x] = inDesign(sx, sy);
            }
        }

        // Light blur for the slope, so jitter does not read as steepness.
        var b = new double[ww * wh];
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
            {
                double sum = 0; int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= ww || yy >= wh) continue;
                        sum += v[yy * ww + xx]; n++;
                    }
                b[y * ww + x] = sum / n;
            }

        double limit = maxValue * SlopeLimit;
        var flat = new bool[ww * wh];
        long designCount = 0;
        for (int y = 1; y < wh - 1; y++)
            for (int x = 1; x < ww - 1; x++)
            {
                int i = y * ww + x;
                if (!design[i]) continue;
                designCount++;
                double gx = (b[i + 1] - b[i - 1]) / 2, gy = (b[i + ww] - b[i - ww]) / 2;
                // Per source pixel: one reduced pixel spans 1/s of them.
                double slope = Math.Sqrt(gx * gx + gy * gy) * s;
                flat[i] = slope < limit;
            }
        if (designCount == 0) return new List<FlatArea>();

        // Connected runs, 4-connected, smallest kept is half a percent of the design.
        var label = new int[ww * wh];
        long minArea = Math.Max(50, (long)(designCount * 0.005));
        var runs = new List<(int Label, List<int> Pixels)>();
        var stack = new Stack<int>();
        int next = 0;
        for (int i = 0; i < flat.Length; i++)
        {
            if (!flat[i] || label[i] != 0) continue;
            next++;
            var pix = new List<int>();
            stack.Push(i);
            label[i] = next;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                pix.Add(p);
                int px = p % ww, py = p / ww;
                if (px > 0) Visit(p - 1);
                if (px < ww - 1) Visit(p + 1);
                if (py > 0) Visit(p - ww);
                if (py < wh - 1) Visit(p + ww);
            }
            if (pix.Count >= minArea) runs.Add((next, pix));

            void Visit(int q)
            {
                if (!flat[q] || label[q] != 0) return;
                label[q] = next;
                stack.Push(q);
            }
        }

        // The design's deepest 3%, to say whether an area is the floor.
        var dvals = new List<int>((int)designCount);
        for (int i = 0; i < design.Length; i++) if (design[i]) dvals.Add(v[i]);
        dvals.Sort();
        int dmin = dvals[0], dmax = dvals[^1];
        double floorTop = dmin + (dmax - dmin) * 0.03;
        double topBottom = dmax - (dmax - dmin) * 0.03;

        var areas = new List<FlatArea>();
        var n9 = new int[9];
        foreach (var (_, pix) in runs.OrderByDescending(r => r.Pixels.Count).ThenBy(r => r.Pixels[0]).Take(maxAreas))
        {
            var vals = pix.Select(p => v[p]).OrderBy(x => x).ToArray();
            var jit = new List<double>(pix.Count);
            double sx = 0, sy = 0;
            var mask = new bool[ww * wh];
            foreach (int p in pix)
            {
                mask[p] = true;
                int px = p % ww, py = p / ww;
                sx += px; sy += py;
                // Pixel-to-pixel jitter: distance from the median of the 3 x 3 around it.
                if (px > 0 && py > 0 && px < ww - 1 && py < wh - 1)
                {
                    int k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            n9[k++] = v[p + dy * ww + dx];
                    Array.Sort(n9);
                    jit.Add(Math.Abs(v[p] - n9[4]));
                }
            }
            jit.Sort();
            int Pct(double q) => vals[Math.Clamp((int)Math.Round(q * (vals.Length - 1)), 0, vals.Length - 1)];
            areas.Add(new FlatArea
            {
                Rank = areas.Count + 1,
                Mask = mask,
                MaskW = ww,
                MaskH = wh,
                Pixels = (long)Math.Round(pix.Count / (s * s)),
                ShareOfDesign = pix.Count / (double)designCount,
                Median = Pct(0.5),
                Low = Pct(0.01),
                High = Pct(0.99),
                Jitter = jit.Count > 0 ? jit[jit.Count / 2] : 0,
                CentreX = sx / pix.Count / s,
                CentreY = sy / pix.Count / s,
                TouchesFloor = Pct(0.5) <= floorTop,
                TouchesTop = Pct(0.5) >= topBottom,
            });
        }
        return areas;
    }

    /// <summary>
    /// Apply flat-area changes to a buffer of any size. Returns pixels changed and the largest
    /// change, in levels.
    /// </summary>
    public static (long Changed, int MaxChange) Apply(ushort[] buf, int w, int h, IReadOnlyList<FlatAction> actions)
    {
        long changed = 0;
        int maxChange = 0;
        foreach (var a in actions)
        {
            if (a.Mode == FlatMode.Leave) continue;
            bool InMask(int x, int y) => a.Mask[(int)((long)y * a.MaskH / h) * a.MaskW + (int)((long)x * a.MaskW / w)];
            bool InBand(int val) => val >= a.Low && val <= a.High;

            ushort[] src = a.Mode == FlatMode.Smooth ? (ushort[])buf.Clone() : buf;
            for (int y = 0; y < h; y++)
            {
                long row = (long)y * w;
                for (int x = 0; x < w; x++)
                {
                    int val = src[row + x];
                    if (!InBand(val) || !InMask(x, y)) continue;

                    int nv;
                    if (a.Mode == FlatMode.Flatten) nv = a.Level;
                    else
                    {
                        // Mean of the in-band neighbours in a 5 x 5 window: jitter averages
                        // out, a slope does not, and an edge leaving the band is never pulled in.
                        long sum = 0; int n = 0;
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= h) continue;
                            long r2 = (long)yy * w;
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= w) continue;
                                int q = src[r2 + xx];
                                if (!InBand(q)) continue;
                                sum += q; n++;
                            }
                        }
                        nv = (int)Math.Round(sum / (double)n);
                    }

                    if (nv != buf[row + x])
                    {
                        maxChange = Math.Max(maxChange, Math.Abs(nv - val));
                        buf[row + x] = (ushort)nv;
                        changed++;
                    }
                }
            }
        }
        return (changed, maxChange);
    }
}
