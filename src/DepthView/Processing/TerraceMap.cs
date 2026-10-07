using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>How one layer edge will look, judged by the treads on either side of it.</summary>
public enum TerraceClass : byte
{
    /// <summary>Not a layer edge.</summary>
    None = 0,

    /// <summary>A tread narrower than the spot on at least one side: the beam smears the step into the slope.</summary>
    Blended = 1,

    /// <summary>Flat treads wider than the spot on both sides: the step stays a step.</summary>
    WiderThanSpot = 2,

    /// <summary>Treads more than three spots wide on both sides: a plain contour line.</summary>
    WiderThan3Spots = 3,
}

/// <summary>
/// Where a job will terrace, and how badly - measured on the map, at a pass count, against a
/// spot size. See <see cref="TerraceMap"/>.
/// </summary>
public sealed class TerraceReport
{
    public int Width, Height, Passes;
    public double PixelsPerMm, MicronsPerPixel, SpotMicrons, TargetDepthMm;

    /// <summary>Depth one pass removes at the deepest point: the height of one step.</summary>
    public double StepMicrons;

    /// <summary>Distinct levels the map itself holds.</summary>
    public int UsedLevels;

    /// <summary>Layer-edge pixels: where the slice a pixel falls in changes.</summary>
    public long Edges;

    /// <summary>Edges with treads wider than the spot on both sides.</summary>
    public long Wider;

    /// <summary>Edges with treads more than three spots wide on both sides.</summary>
    public long Wider3;

    /// <summary>Tread width at the edges, in microns: the narrower of the two sides.</summary>
    public double MedianTreadMicrons, P90TreadMicrons;

    /// <summary>The 90th percentile reached the measuring cap, so the true figure is larger.</summary>
    public bool TreadCapped;

    /// <summary>
    /// Passes at which nine edges in ten would have treads no wider than the spot, if the map
    /// can supply that many distinct depths; null when it cannot (<see cref="LimitedByLevels"/>).
    /// </summary>
    public int? PassesToBlend90;

    /// <summary>
    /// More passes will not help: the map does not hold enough distinct levels, so its own
    /// steps are the terraces. Only a finer map fixes that.
    /// </summary>
    public bool LimitedByLevels;

    public double Seconds;

    /// <summary>
    /// Radius in pixels of the circle measured: the blank, which spans the short side and is
    /// centred, exactly as the pixels-per-mm figure assumes. Zero when the whole canvas was
    /// measured.
    /// </summary>
    public double BlankRadiusPx;

    /// <summary>One <see cref="TerraceClass"/> per pixel, when it was asked for.</summary>
    public byte[]? Classes;

    public double EdgeLengthMm => PixelsPerMm > 0 ? Edges / PixelsPerMm : 0;
    public double ShareWider => Edges == 0 ? 0 : (double)Wider / Edges;
    public double ShareWider3 => Edges == 0 ? 0 : (double)Wider3 / Edges;
}

/// <summary>
/// The terrace map (TODO 7.3): where the layer edges of a sliced job land, and which of them
/// will show as steps in the metal.
///
/// A slicer cuts a map as a stack of flat layers. Each layer edge is a step one pass high;
/// whether it reads as a step or as part of a smooth slope depends on how wide the flat treads
/// either side of it are compared with the beam. Narrower than the spot and the beam smears
/// neighbouring steps together into a slope; wider, and the staircase survives. On a steep
/// surface the edges crowd together and blend; on a gentle one - a cheek, a neck, a sky - they
/// spread apart and show as contour lines, however many bits the file had.
///
/// So the measurement is the tread, walked directly on the sliced map rather than inferred from
/// a slope: from each layer edge, along the direction the layers climb, count the pixels that
/// stay in the same layer on each side. That one walk covers both causes of terracing - too few
/// passes for a smooth map, and a map whose own levels are coarser than the passes (an 8-bit map
/// at a thousand layers terraces on its own steps, and more passes cannot fix it).
///
/// Geometry only. Whether a tread of a given width is visible to the eye depends on the step
/// height, the finish and the light; the spot is the honest dividing line the geometry
/// supports, and the result says no more than that.
/// </summary>
public static class TerraceMap
{
    /// <summary>
    /// Measure the terraces of <paramref name="grey"/> cut at <paramref name="passes"/>.
    /// Black is deepest. Slices are assigned exactly as the rest of DepthView counts them
    /// (<see cref="TuneJob.DepthsAt"/>), so a figure here agrees with every other count.
    ///
    /// Only the blank is measured: the circle spanning the short side, centred, which is the
    /// same blank the pixels-per-mm figure is worked out from. A map's corners are not on the
    /// coin, and a shaded background there terraces in contour lines that would never be cut -
    /// counting them made a coin read worse than it is. <paramref name="wholeCanvas"/> measures
    /// everything, for a job that is not cut on a round blank.
    /// </summary>
    public static TerraceReport Measure(ushort[] grey, int width, int height, int maxValue, int passes,
                                        double pixelsPerMm, double targetDepthMm, double spotMicrons,
                                        bool keepClasses = false, CancellationToken cancel = default,
                                        bool wholeCanvas = false)
    {
        var sw = Stopwatch.StartNew();
        passes = Math.Max(2, passes);
        maxValue = Math.Max(1, maxValue);
        double pxUm = pixelsPerMm > 0 ? 1000.0 / pixelsPerMm : 10;
        double spot = Math.Max(0.1, spotMicrons);

        var r = new TerraceReport
        {
            Width = width, Height = height, Passes = passes,
            PixelsPerMm = pixelsPerMm, MicronsPerPixel = pxUm,
            SpotMicrons = spot, TargetDepthMm = targetDepthMm,
            StepMicrons = targetDepthMm * 1000.0 / passes,
        };

        // Slice of every level, once.
        var lut = new int[maxValue + 1];
        for (int v = 0; v <= maxValue; v++)
            lut[v] = Math.Clamp((int)Math.Round((double)v / maxValue * (passes - 1)), 0, passes - 1);

        // The blank: centred, spanning the short side. Pixel centres within it count.
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        double radius = wholeCanvas ? 0 : Math.Min(width, height) / 2.0;
        double r2 = radius * radius;
        r.BlankRadiusPx = radius;
        bool On(int x, int y)
        {
            if (radius <= 0) return true;
            double dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= r2;
        }

        var present = new bool[maxValue + 1];
        for (int y = 0; y < height; y++)
        {
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
                if (On(x, y)) present[Math.Min(grey[row + x], (ushort)maxValue)] = true;
        }
        foreach (var p in present) if (p) r.UsedLevels++;

        // Treads are walked up to four spots and then called "wide": past that the answer no
        // longer changes anything, and an unbounded walk across a flat field costs a lot.
        double spotPx = spot / pxUm;
        int cap = Math.Clamp((int)Math.Ceiling(4 * spotPx) + 2, 6, 4096);
        double wideUm = spot, wide3Um = 3 * spot;

        byte[]? classes = keepClasses ? new byte[(long)width * height] : null;
        var hist = new long[cap + 2];
        long edges = 0, wider = 0, wider3 = 0;
        object gate = new();

        int Q(int x, int y) => lut[Math.Min((int)grey[(long)y * width + x], maxValue)];

        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancel },
            () => (hist: new long[cap + 2], e: 0L, w1: 0L, w3: 0L),
            (y, _, acc) =>
            {
                for (int x = 0; x < width; x++)
                {
                    // Off the blank there is no metal, so no edge - and the blank's own
                    // boundary is not a step either.
                    if (!On(x, y)) continue;
                    int qa = Q(x, y);
                    int qr = x + 1 < width && On(x + 1, y) ? Q(x + 1, y) : qa;
                    int qd = y + 1 < height && On(x, y + 1) ? Q(x, y + 1) : qa;
                    if (qr == qa && qd == qa) continue;

                    // The direction the layers climb here, from a Sobel on the slice numbers.
                    // A diagonal contour is walked across, not along a pixel axis, so its tread
                    // is not overstated by up to root two.
                    int xm = Math.Max(0, x - 1), xp = Math.Min(width - 1, x + 1);
                    int ym = Math.Max(0, y - 1), yp = Math.Min(height - 1, y + 1);
                    double gx = (Q(xp, ym) + 2 * Q(xp, y) + Q(xp, yp)) - (Q(xm, ym) + 2 * Q(xm, y) + Q(xm, yp));
                    double gy = (Q(xm, yp) + 2 * Q(x, yp) + Q(xp, yp)) - (Q(xm, ym) + 2 * Q(x, ym) + Q(xp, ym));
                    if (gx == 0 && gy == 0)
                    {
                        if (qr != qa) gx = qr - qa; else gy = qd - qa;
                    }
                    double len = Math.Sqrt(gx * gx + gy * gy);
                    double nx = gx / len, ny = gy / len;

                    // Back down the slope: the tread this pixel sits on.
                    int lower = 0;
                    for (int t = 1; t <= cap; t++)
                    {
                        int px = (int)Math.Round(x - t * nx), py = (int)Math.Round(y - t * ny);
                        if (px < 0 || py < 0 || px >= width || py >= height || !On(px, py) || Q(px, py) != qa) break;
                        lower = t;
                    }
                    lower += 1;

                    // Up the slope: past this edge, then across the next tread.
                    int upper = 0, qu = int.MinValue, start = 0;
                    for (int t = 1; t <= cap + 2; t++)
                    {
                        int px = (int)Math.Round(x + t * nx), py = (int)Math.Round(y + t * ny);
                        if (px < 0 || py < 0 || px >= width || py >= height || !On(px, py)) break;
                        int q = Q(px, py);
                        if (qu == int.MinValue)
                        {
                            if (q == qa) continue;
                            qu = q; start = t;
                        }
                        else if (q != qu) break;
                        upper = t - start + 1;
                    }
                    if (upper == 0) upper = cap;   // nothing beyond: the edge of the blank

                    int tread = Math.Min(Math.Min(lower, upper), cap);
                    double um = tread * pxUm;
                    acc.hist[tread]++;
                    acc.e++;
                    var cls = TerraceClass.Blended;
                    if (um > wideUm) { acc.w1++; cls = TerraceClass.WiderThanSpot; }
                    if (um > wide3Um) { acc.w3++; cls = TerraceClass.WiderThan3Spots; }
                    if (classes is not null) classes[(long)y * width + x] = (byte)cls;
                }
                return acc;
            },
            acc =>
            {
                lock (gate)
                {
                    for (int i = 0; i < hist.Length; i++) hist[i] += acc.hist[i];
                    edges += acc.e; wider += acc.w1; wider3 += acc.w3;
                }
            });

        r.Edges = edges;
        r.Wider = wider;
        r.Wider3 = wider3;
        r.Classes = classes;

        if (edges > 0)
        {
            int Pct(double f)
            {
                long need = (long)Math.Ceiling(edges * f), run = 0;
                for (int i = 0; i < hist.Length; i++) { run += hist[i]; if (run >= need) return i; }
                return hist.Length - 1;
            }
            int median = Pct(0.5), p90 = Pct(0.9);
            r.MedianTreadMicrons = median * pxUm;
            r.P90TreadMicrons = p90 * pxUm;
            r.TreadCapped = p90 >= cap;

            // A smooth map's treads shrink in proportion to the pass count, so the passes that
            // bring nine edges in ten down to the spot follow directly. A map with fewer levels
            // than that cannot supply the depths, and then its own steps are what terraces.
            if (r.P90TreadMicrons <= spot)
                r.PassesToBlend90 = passes;
            else
            {
                int need = (int)Math.Ceiling(passes * r.P90TreadMicrons / spot);
                if (need > r.UsedLevels) r.LimitedByLevels = true;
                else r.PassesToBlend90 = need;
            }
        }

        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    /// <summary>
    /// The map, dimmed, with the layer edges that will show in colour - amber where the treads
    /// are wider than the spot, red where they are more than three spots wide. BGRA, opaque.
    ///
    /// At full size the edges are drawn as the lines they are. Reduced for a preview pane, a
    /// coin at a hundred passes has an edge in nearly every block, so drawing "any edge that
    /// shows" would paint the whole coin; instead each block is tinted by the share of its edges
    /// that will show. A textured area whose many edges blend stays grey, a smooth cheek whose
    /// few edges all show turns amber or red, and a flat area with no edges stays grey too.
    /// </summary>
    /// <remarks>
    /// Off the blank (pass the report's <see cref="TerraceReport.BlankRadiusPx"/>) the map is
    /// drawn faint, so it reads as "not on the coin" rather than as part of the design that
    /// happens to have no steps.
    /// </remarks>
    public static byte[] Overlay(ushort[] grey, int width, int height, int maxValue, byte[] classes,
                                 int outWidth, int outHeight, double blankRadiusPx = 0)
    {
        long n = (long)outWidth * outHeight;
        var buf = new byte[n * 4];
        bool lines = width < 2 * outWidth;
        double bcx = (width - 1) / 2.0, bcy = (height - 1) / 2.0, br2 = blankRadiusPx * blankRadiusPx;

        var all = new int[n];
        var show = new int[n];
        var red = new int[n];
        for (int y = 0; y < height; y++)
        {
            int oy = (int)((long)y * outHeight / height);
            long row = (long)y * width, orow = (long)oy * outWidth;
            for (int x = 0; x < width; x++)
            {
                byte c = classes[row + x];
                if (c == 0) continue;
                long o = orow + (int)((long)x * outWidth / width);
                all[o]++;
                if (c >= (byte)TerraceClass.WiderThanSpot) show[o]++;
                if (c == (byte)TerraceClass.WiderThan3Spots) red[o]++;
            }
        }

        for (int oy = 0; oy < outHeight; oy++)
        {
            int sy = (int)((long)oy * height / outHeight);
            for (int ox = 0; ox < outWidth; ox++)
            {
                int sx = (int)((long)ox * width / outWidth);
                long o = (long)oy * outWidth + ox;
                long d = o * 4;

                // The map at 55% so the colours are the loudest thing on it.
                double g = grey[(long)sy * width + sx] * 140.0 / Math.Max(1, maxValue);
                double alpha = 0, isRed = 0;

                if (blankRadiusPx > 0)
                {
                    double dx = sx - bcx, dy = sy - bcy;
                    if (dx * dx + dy * dy > br2)
                    {
                        byte f = (byte)Math.Round(0x22 + g * 0.2);
                        buf[d] = f; buf[d + 1] = f; buf[d + 2] = f; buf[d + 3] = 255;
                        continue;
                    }
                }

                if (lines)
                {
                    if (show[o] > 0) { alpha = 1; isRed = red[o] > 0 ? 1 : 0; }
                }
                else
                {
                    // Pooled over the 3 x 3 neighbourhood, so one block's handful of edges
                    // does not decide its colour on its own.
                    long a = 0, s = 0, rr = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int yy = oy + dy, xx = ox + dx;
                            if (yy < 0 || xx < 0 || yy >= outHeight || xx >= outWidth) continue;
                            long k = (long)yy * outWidth + xx;
                            a += all[k]; s += show[k]; rr += red[k];
                        }
                    if (a > 0 && s > 0)
                    {
                        double share = (double)s / a;
                        alpha = Math.Clamp((share - 0.1) / 0.5, 0, 1) * 0.9;
                        isRed = (double)rr / s;
                    }
                }

                // Amber #F0B45C to red #E8484B by the share of the showing edges that are
                // more than three spots wide.
                double cr = 0xF0 + (0xE8 - 0xF0) * isRed, cg = 0xB4 + (0x48 - 0xB4) * isRed, cb = 0x5C + (0x4B - 0x5C) * isRed;
                buf[d] = (byte)Math.Round(g + (cb - g) * alpha);
                buf[d + 1] = (byte)Math.Round(g + (cg - g) * alpha);
                buf[d + 2] = (byte)Math.Round(g + (cr - g) * alpha);
                buf[d + 3] = 255;
            }
        }
        return buf;
    }

    /// <summary>
    /// Levels along a straight line, one sample per pixel of length, nearest neighbour - the
    /// file's own values, never interpolated ones.
    /// </summary>
    public static ushort[] Profile(ushort[] grey, int width, int height, double x0, double y0, double x1, double y1)
    {
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int n = Math.Max(2, (int)Math.Ceiling(len) + 1);
        var s = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            int x = Math.Clamp((int)Math.Round(x0 + t * (x1 - x0)), 0, width - 1);
            int y = Math.Clamp((int)Math.Round(y0 + t * (y1 - y0)), 0, height - 1);
            s[i] = grey[(long)y * width + x];
        }
        return s;
    }

    /// <summary>The slice a level falls in at a pass count, as everywhere else in DepthView.</summary>
    public static int SliceOf(int level, int maxValue, int passes)
        => Math.Clamp((int)Math.Round((double)level / Math.Max(1, maxValue) * (passes - 1)), 0, passes - 1);
}
