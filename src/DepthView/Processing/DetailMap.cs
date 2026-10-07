using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>What the detail check found, on the blank.</summary>
public sealed class DetailReport
{
    public int Width, Height, Passes;
    public double PixelsPerMm, MicronsPerPixel, SpotMicrons, TargetDepthMm, BlankRadiusPx;

    /// <summary>The disc radii used, in pixels: half a spot and a whole spot (one and two spots across).</summary>
    public double SpotRadiusPx, TwoSpotRadiusPx;

    /// <summary>True when the spot is narrower than a pixel: every feature the file can hold is wider than the beam.</summary>
    public bool FinerThanPixels;

    public long BlankPixels;

    /// <summary>Raised features (ridges, dots, strokes) and recessed ones (grooves, pits) narrower than one spot.</summary>
    public long RaisedUnderSpot, RecessedUnderSpot;

    /// <summary>The same, narrower than two spots but not narrower than one.</summary>
    public long RaisedUnder2Spots, RecessedUnder2Spots;

    /// <summary>The tallest such feature narrower than one spot, in microns of depth.</summary>
    public double TallestUnderSpotMicrons;

    public double Seconds;

    /// <summary>Per pixel: 0 none, 1 narrower than two spots, 2 narrower than one, when asked for.</summary>
    public byte[]? Classes;

    public double ShareUnderSpot => BlankPixels == 0 ? 0 : (RaisedUnderSpot + RecessedUnderSpot) / (double)BlankPixels;
    public double ShareUnder2Spots => BlankPixels == 0 ? 0 : (RaisedUnder2Spots + RecessedUnder2Spots) / (double)BlankPixels;
    public double AreaUnderSpotMm2 => PixelsPerMm <= 0 ? 0 : (RaisedUnderSpot + RecessedUnderSpot) / (PixelsPerMm * PixelsPerMm);
}

/// <summary>
/// Detail finer than the spot (TODO 9.7): which raised and recessed features of the design are
/// narrower than the beam, and so will be rounded, softened or lost however carefully the job is
/// run - lettering edges, thin lines, small dots, stippling.
///
/// Grey-scale morphology answers it directly. An opening with a flat disc as wide as the spot
/// removes every raised feature the disc cannot fit inside; a closing fills every recess it cannot
/// fit into. What they remove (the top-hat and black-hat) is exactly the detail narrower than the
/// beam. A second pair with a disc two spots wide marks the detail at risk. Only differences of at
/// least one layer count: a feature lower than one pass cuts is not a feature of the job.
///
/// Geometry against a spot size, nothing more: whether a softened stroke still reads depends on
/// its height and the finish. The spot is a setting until it is measured (TODO 7.1) - and that is
/// the honest way to compare lenses: the same file at two spot sizes.
/// </summary>
public static class DetailMap
{
    public static DetailReport Measure(ushort[] grey, int width, int height, int maxValue, int passes,
                                       double pixelsPerMm, double targetDepthMm, double spotMicrons,
                                       bool keepClasses = false, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        passes = Math.Max(2, passes);
        maxValue = Math.Max(1, maxValue);
        double pxUm = pixelsPerMm > 0 ? 1000.0 / pixelsPerMm : 10;
        var r = new DetailReport
        {
            Width = width, Height = height, Passes = passes,
            PixelsPerMm = pixelsPerMm, MicronsPerPixel = pxUm,
            SpotMicrons = spotMicrons, TargetDepthMm = targetDepthMm,
            BlankRadiusPx = Math.Min(width, height) / 2.0,
            SpotRadiusPx = spotMicrons / pxUm / 2.0,
            TwoSpotRadiusPx = spotMicrons / pxUm,
        };

        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0, r2 = r.BlankRadiusPx * r.BlankRadiusPx;
        bool On(int x, int y) { double dx = x - cx, dy = y - cy; return dx * dx + dy * dy <= r2; }
        long blank = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (On(x, y)) blank++;
        r.BlankPixels = blank;

        // A disc under one pixel across fits inside everything a pixel grid can draw.
        r.FinerThanPixels = r.TwoSpotRadiusPx < 1.0;
        byte[]? classes = keepClasses ? new byte[(long)width * height] : null;
        r.Classes = classes;
        if (r.FinerThanPixels)
        {
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        var f = new float[grey.Length];
        for (long i = 0; i < grey.Length; i++) f[i] = grey[i];

        float step = (float)(maxValue / (double)(passes - 1));
        var open1 = r.SpotRadiusPx >= 1 ? Morphology.Open(f, width, height, r.SpotRadiusPx, cancel) : null;
        var close1 = r.SpotRadiusPx >= 1 ? Morphology.Close(f, width, height, r.SpotRadiusPx, cancel) : null;
        var open2 = Morphology.Open(f, width, height, r.TwoSpotRadiusPx, cancel);
        var close2 = Morphology.Close(f, width, height, r.TwoSpotRadiusPx, cancel);

        long ru1 = 0, rc1 = 0, ru2 = 0, rc2 = 0;
        float tallest = 0;
        object gate = new();
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancel },
            () => (a: 0L, b: 0L, c: 0L, d: 0L, t: 0f),
            (y, _, acc) =>
            {
                for (int x = 0; x < width; x++)
                {
                    if (!On(x, y)) continue;
                    long i = (long)y * width + x;
                    float v = f[i];
                    float top1 = open1 is null ? 0 : v - open1[i];
                    float bot1 = close1 is null ? 0 : close1[i] - v;
                    float top2 = v - open2[i], bot2 = close2[i] - v;
                    byte cls = 0;
                    if (top1 >= step || bot1 >= step)
                    {
                        cls = 2;
                        if (top1 >= bot1) acc.a++; else acc.b++;
                        float hgt = Math.Max(top1, bot1);
                        if (hgt > acc.t) acc.t = hgt;
                    }
                    else if (top2 >= step || bot2 >= step)
                    {
                        cls = 1;
                        if (top2 >= bot2) acc.c++; else acc.d++;
                    }
                    if (classes is not null) classes[i] = cls;
                }
                return acc;
            },
            acc =>
            {
                lock (gate)
                {
                    ru1 += acc.a; rc1 += acc.b; ru2 += acc.c; rc2 += acc.d;
                    if (acc.t > tallest) tallest = acc.t;
                }
            });

        r.RaisedUnderSpot = ru1;
        r.RecessedUnderSpot = rc1;
        r.RaisedUnder2Spots = ru2;
        r.RecessedUnder2Spots = rc2;
        r.TallestUnderSpotMicrons = tallest / maxValue * targetDepthMm * 1000.0;
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    /// <summary>
    /// The map dimmed, with detail narrower than the spot in red and narrower than two spots in
    /// amber; off the blank drawn faint. Reduced for a preview, each block is tinted by the share
    /// of its pixels that are marked, pooled over 3 x 3 blocks, so a fine texture reads as a wash
    /// and a single thin stroke still shows.
    /// </summary>
    public static byte[] Overlay(ushort[] grey, int width, int height, int maxValue, byte[] classes,
                                 int outWidth, int outHeight, double blankRadiusPx)
        => ClassOverlay.Draw(grey, width, height, maxValue, classes, outWidth, outHeight, blankRadiusPx);
}

/// <summary>
/// The shared drawing for the per-pixel checks (detail, noise, flattened peaks): the map dimmed,
/// class 2 in red, class 1 in amber, blended by each block's share of marked pixels, faint off
/// the blank. The terrace map has its own, because its classes are edges rather than areas.
/// </summary>
public static class ClassOverlay
{
    public static byte[] Draw(ushort[] grey, int width, int height, int maxValue, byte[] classes,
                              int outWidth, int outHeight, double blankRadiusPx, double gain = 4.0)
    {
        long n = (long)outWidth * outHeight;
        var buf = new byte[n * 4];
        var all = new int[n];
        var hot = new int[n];
        var red = new int[n];
        for (int y = 0; y < height; y++)
        {
            int oy = (int)((long)y * outHeight / height);
            for (int x = 0; x < width; x++)
            {
                long o = (long)oy * outWidth + (int)((long)x * outWidth / width);
                all[o]++;
                byte c = classes[(long)y * width + x];
                if (c >= 1) hot[o]++;
                if (c >= 2) red[o]++;
            }
        }

        double bcx = (width - 1) / 2.0, bcy = (height - 1) / 2.0, br2 = blankRadiusPx * blankRadiusPx;
        bool direct = width < 2 * outWidth;
        for (int oy = 0; oy < outHeight; oy++)
        {
            int sy = (int)((long)oy * height / outHeight);
            for (int ox = 0; ox < outWidth; ox++)
            {
                int sx = (int)((long)ox * width / outWidth);
                long o = (long)oy * outWidth + ox, d = o * 4;
                double g = grey[(long)sy * width + sx] * 140.0 / Math.Max(1, maxValue);

                if (blankRadiusPx > 0 && Sq(sx - bcx) + Sq(sy - bcy) > br2)
                {
                    byte fv = (byte)Math.Round(0x22 + g * 0.2);
                    buf[d] = fv; buf[d + 1] = fv; buf[d + 2] = fv; buf[d + 3] = 255;
                    continue;
                }

                double alpha = 0, isRed = 0;
                long a = 0, h = 0, rr = 0;
                int reach = direct ? 0 : 1;
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int yy = oy + dy, xx = ox + dx;
                        if (yy < 0 || xx < 0 || yy >= outHeight || xx >= outWidth) continue;
                        long k = (long)yy * outWidth + xx;
                        a += all[k]; h += hot[k]; rr += red[k];
                    }
                if (a > 0 && h > 0)
                {
                    alpha = direct ? 1.0 : Math.Clamp(h / (double)a * gain, 0.25, 1) * 0.92;
                    isRed = rr / (double)h;
                }

                double cr = 0xF0 + (0xE8 - 0xF0) * isRed, cg = 0xB4 + (0x48 - 0xB4) * isRed, cb = 0x5C + (0x4B - 0x5C) * isRed;
                buf[d] = (byte)Math.Round(g + (cb - g) * alpha);
                buf[d + 1] = (byte)Math.Round(g + (cg - g) * alpha);
                buf[d + 2] = (byte)Math.Round(g + (cr - g) * alpha);
                buf[d + 3] = 255;
            }
        }
        return buf;
    }

    private static double Sq(double v) => v * v;
}
