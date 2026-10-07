using System;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>
/// Grey-scale morphology with a flat disc: the maximum (dilation) or minimum (erosion) of a
/// height field over a disc around each pixel, and the opening and closing built from them.
///
/// The disc is a stack of horizontal runs, one per row offset, and each run is a running
/// max or min done with the van Herk / Gil-Werman trick - two passes per row, constant work
/// per pixel whatever the run length. So a disc of radius r costs (2r + 1) row passes per
/// output row instead of the r squared of a direct scan.
///
/// Two users. The detail check (finer than the spot) opens and closes with a disc the size
/// of the beam: whatever the disc cannot fit inside is narrower than the beam. The finishing
/// preview closes with a disc the size of a polishing tool: the closing is the surface the
/// tool can actually ride on, and how far a point lies below it is how hard the tool has to
/// reach to touch it.
/// </summary>
public static class Morphology
{
    /// <summary>Dilation (max) or erosion (min) over a flat disc of radius <paramref name="radius"/> pixels.</summary>
    public static float[] Disc(float[] src, int w, int h, double radius, bool dilate, CancellationToken cancel = default)
    {
        int r = (int)Math.Floor(Math.Max(0, radius));
        var dst = new float[src.Length];
        if (r == 0)
        {
            Array.Copy(src, dst, src.Length);
            return dst;
        }

        // Half-width of the disc's run on each row offset.
        var half = new int[2 * r + 1];
        for (int dy = -r; dy <= r; dy++)
            half[dy + r] = (int)Math.Floor(Math.Sqrt(Math.Max(0, radius * radius - dy * dy)));

        float pad = dilate ? float.NegativeInfinity : float.PositiveInfinity;

        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel },
            () => (acc: new float[w], run: new float[w], g: new float[w + 2 * r + 2], hh: new float[w + 2 * r + 2]),
            (y, _, t) =>
            {
                Array.Fill(t.acc, pad);
                for (int dy = -r; dy <= r; dy++)
                {
                    int sy = y + dy;
                    if (sy < 0 || sy >= h) continue;
                    RunFilter(src, (long)sy * w, w, half[dy + r], dilate, pad, t.run, t.g, t.hh);
                    if (dilate) { for (int x = 0; x < w; x++) if (t.run[x] > t.acc[x]) t.acc[x] = t.run[x]; }
                    else { for (int x = 0; x < w; x++) if (t.run[x] < t.acc[x]) t.acc[x] = t.run[x]; }
                }
                Array.Copy(t.acc, 0, dst, (long)y * w, w);
                return t;
            },
            _ => { });
        return dst;
    }

    /// <summary>Opening: erosion then dilation. Removes raised features the disc does not fit inside.</summary>
    public static float[] Open(float[] src, int w, int h, double radius, CancellationToken cancel = default)
        => Disc(Disc(src, w, h, radius, dilate: false, cancel), w, h, radius, dilate: true, cancel);

    /// <summary>Closing: dilation then erosion. Fills recesses the disc does not fit into.</summary>
    public static float[] Close(float[] src, int w, int h, double radius, CancellationToken cancel = default)
        => Disc(Disc(src, w, h, radius, dilate: true, cancel), w, h, radius, dilate: false, cancel);

    /// <summary>
    /// Closing with a disc that may be far larger than is affordable at full resolution - a
    /// polishing block wider than the coin. Above <paramref name="maxDirect"/> pixels the
    /// field is max-pooled down, closed there, and brought back bilinearly; the result is
    /// then never allowed below the surface itself, which a closing never is. Close enough
    /// for a preview of where a broad, stiff tool touches, and nothing else uses it.
    /// </summary>
    public static float[] CloseLarge(float[] src, int w, int h, double radius, int maxDirect = 40, CancellationToken cancel = default)
    {
        if (radius <= maxDirect) return Close(src, w, h, radius, cancel);

        int f = (int)Math.Ceiling(radius / maxDirect);
        int cw = Math.Max(1, (w + f - 1) / f), ch = Math.Max(1, (h + f - 1) / f);
        var coarse = new float[(long)cw * ch];
        for (int cy = 0; cy < ch; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                float m = float.NegativeInfinity;
                for (int y = cy * f; y < Math.Min(h, cy * f + f); y++)
                    for (int x = cx * f; x < Math.Min(w, cx * f + f); x++)
                        if (src[(long)y * w + x] > m) m = src[(long)y * w + x];
                coarse[(long)cy * cw + cx] = m;
            }

        var closed = Close(coarse, cw, ch, radius / f, cancel);
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            double fy = Math.Clamp((y + 0.5) / f - 0.5, 0, ch - 1);
            int y0 = (int)fy, y1 = Math.Min(ch - 1, y0 + 1);
            double ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x + 0.5) / f - 0.5, 0, cw - 1);
                int x0 = (int)fx, x1 = Math.Min(cw - 1, x0 + 1);
                double tx = fx - x0;
                double a = closed[(long)y0 * cw + x0] + (closed[(long)y0 * cw + x1] - closed[(long)y0 * cw + x0]) * tx;
                double b = closed[(long)y1 * cw + x0] + (closed[(long)y1 * cw + x1] - closed[(long)y1 * cw + x0]) * tx;
                float v = (float)(a + (b - a) * ty);
                float s = src[(long)y * w + x];
                dst[(long)y * w + x] = v < s ? s : v;
            }
        });
        return dst;
    }

    /// <summary>
    /// Running max or min of one row over a centred window of 2k + 1, van Herk / Gil-Werman:
    /// a forward pass within blocks of the window length and a backward pass, then each output
    /// is the better of one value from each. Off the row's ends counts as <paramref name="pad"/>.
    /// </summary>
    private static void RunFilter(float[] src, long offset, int w, int k, bool max, float pad,
                                  float[] dst, float[] g, float[] hh)
    {
        if (k <= 0)
        {
            Array.Copy(src, offset, dst, 0, w);
            return;
        }

        int m = 2 * k + 1;
        int n = w + 2 * k;                       // padded length: k pads each side
        float At(int i) { int x = i - k; return x < 0 || x >= w ? pad : src[offset + x]; }

        for (int i = 0; i < n; i++)
        {
            float v = At(i);
            if (i % m == 0) g[i] = v;
            else g[i] = max ? (v > g[i - 1] ? v : g[i - 1]) : (v < g[i - 1] ? v : g[i - 1]);
        }
        for (int i = n - 1; i >= 0; i--)
        {
            float v = At(i);
            if (i == n - 1 || (i + 1) % m == 0) hh[i] = v;
            else hh[i] = max ? (v > hh[i + 1] ? v : hh[i + 1]) : (v < hh[i + 1] ? v : hh[i + 1]);
        }
        // Window over padded indices [x, x + m - 1] is centred on source pixel x.
        for (int x = 0; x < w; x++)
        {
            float a = hh[x], b = g[x + m - 1];
            dst[x] = max ? (a > b ? a : b) : (a < b ? a : b);
        }
    }
}
