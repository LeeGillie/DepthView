using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>What the noise map found, on the blank.</summary>
public sealed class NoiseReport
{
    public int Width, Height, Passes;
    public double PixelsPerMm, TargetDepthMm, BlankRadiusPx;
    public int WindowPx;

    /// <summary>One pass's share of the depth, in levels: noise below this cuts as nothing.</summary>
    public double StepLevels;

    public long BlankPixels;

    /// <summary>Pixels where the surface carries noise of at least half a layer, and of at least two layers.</summary>
    public long Noisy, VeryNoisy;

    /// <summary>Median noise where it was found, in levels and in microns of depth.</summary>
    public double MedianNoiseLevels, MedianNoiseMicrons;

    public double Seconds;

    /// <summary>Per pixel: 0 clean, 1 noisy, 2 very noisy, when asked for.</summary>
    public byte[]? Classes;

    public double ShareNoisy => BlankPixels == 0 ? 0 : Noisy / (double)BlankPixels;
    public double ShareVeryNoisy => BlankPixels == 0 ? 0 : VeryNoisy / (double)BlankPixels;
}

/// <summary>
/// A noise map (TODO 9.3): where pixel noise sits on a surface that should be smooth - skin, sky,
/// a polished field - as opposed to fine texture that is real detail. It answers "where should I
/// clean up before cutting" without blurring anything, which is the point: a blur removes the
/// scales and stitching along with the speckle.
///
/// Two measurements per window. The noise is Immerkaer's estimate (a Laplacian difference that
/// ignores any plane, so a smooth slope reads zero), taken as a median so edges do not count. The same estimate on a 3 x 3 average of the
/// map separates noise from detail: averaging nine independent pixels all but erases white noise's
/// Laplacian, but leaves texture several pixels across mostly standing. Where the estimate
/// collapses under averaging, the window is noise; where it survives, it is detail and is left
/// alone. Texture only two or three pixels across is indistinguishable from noise this way.
///
/// Speckle smaller than half a layer is not cut at all, so the threshold is in layers at the
/// current pass count. A heuristic, and labelled as one.
/// </summary>
public static class NoiseMap
{
    public static NoiseReport Measure(ushort[] grey, int width, int height, int maxValue, int passes,
                                      double pixelsPerMm, double targetDepthMm,
                                      bool keepClasses = false, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();
        passes = Math.Max(2, passes);
        maxValue = Math.Max(1, maxValue);
        var r = new NoiseReport
        {
            Width = width, Height = height, Passes = passes,
            PixelsPerMm = pixelsPerMm, TargetDepthMm = targetDepthMm,
            BlankRadiusPx = Math.Min(width, height) / 2.0,
            StepLevels = maxValue / (double)(passes - 1),
            // About a quarter of a millimetre, and never less than 7 pixels.
            WindowPx = Math.Max(7, (int)Math.Round(pixelsPerMm * 0.25) | 1),
        };

        // Absolute Laplacian difference of the map and of its 3 x 3 average, per pixel.
        var lap = new float[grey.Length];
        var lapAvg = new float[grey.Length];
        var avg = new float[grey.Length];
        Parallel.For(1, height - 1, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                long i = (long)y * width + x;
                double s = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        s += grey[i + dy * width + dx];
                avg[i] = (float)(s / 9.0);
                lap[i] = (float)Math.Abs(L(grey, i, width));
            }
        });
        Parallel.For(2, height - 2, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (int x = 2; x < width - 2; x++)
            {
                long i = (long)y * width + x;
                lapAvg[i] = (float)Math.Abs(L(avg, i, width));
            }
        });

        // Per window, the MEDIAN absolute Laplacian, of the map and of its average. A median,
        // not a mean: a crisp edge (the rim, the arch, a petal) puts a large Laplacian on the
        // few pixels along it and none elsewhere, so a mean calls every edge noise while a
        // median ignores it. Noise is everywhere in its window, which is what a median sees.
        // Windows overlap by half; each pixel takes the nearest window's verdict.
        int win = r.WindowPx, stride = Math.Max(1, win / 2), half = win / 2;
        int tw = (width + stride - 1) / stride, th = (height + stride - 1) / stride;
        var tileNoise = new float[(long)tw * th];
        // |L| of white noise of sigma is |N(0, 6 sigma)|, whose median is 0.6745 x 6 sigma.
        const double k = 1.0 / (0.6745 * 6.0);
        Parallel.For(0, th, new ParallelOptions { CancellationToken = cancel },
            () => (a: new float[win * win], b: new float[win * win]),
            (ty, _, buf) =>
            {
                for (int tx = 0; tx < tw; tx++)
                {
                    int cxp = tx * stride, cyp = ty * stride;
                    int x0 = Math.Max(2, cxp - half), x1 = Math.Min(width - 3, cxp + half);
                    int y0 = Math.Max(2, cyp - half), y1 = Math.Min(height - 3, cyp + half);
                    int n = 0;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            long i = (long)y * width + x;
                            buf.a[n] = lap[i];
                            buf.b[n] = lapAvg[i];
                            n++;
                        }
                    if (n < 9) continue;
                    double sigma = Median(buf.a, n) * k;
                    if (sigma <= 0) continue;
                    double sigmaAvg = Median(buf.b, n) * k;
                    // Under a 3 x 3 average the Laplacian of white noise falls to 0.074 of
                    // itself; a texture six pixels across keeps 0.44, twelve pixels 0.83
                    // (calculated). A window that keeps little is noise; half or more, detail.
                    double survive = sigmaAvg / sigma;
                    double noiseShare = Math.Clamp((0.45 - survive) / 0.35, 0, 1);
                    tileNoise[(long)ty * tw + tx] = (float)(sigma * noiseShare);
                }
                return buf;
            },
            _ => { });

        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0, br2 = r.BlankRadiusPx * r.BlankRadiusPx;
        byte[]? classes = keepClasses ? new byte[grey.Length] : null;
        long blank = 0, noisy = 0, very = 0;
        var hist = new long[2048];
        object gate = new();
        double histScale = 2047.0 / Math.Max(1, r.StepLevels * 8);

        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancel },
            () => (b: 0L, n: 0L, v: 0L, h: new long[2048]),
            (y, _, acc) =>
            {
                int ty = Math.Min(th - 1, (y + stride / 2) / stride);
                for (int x = 0; x < width; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > br2) continue;
                    acc.b++;
                    int tx = Math.Min(tw - 1, (x + stride / 2) / stride);
                    double noise = tileNoise[(long)ty * tw + tx];
                    if (noise < r.StepLevels * 0.5) continue;
                    byte cls = noise >= r.StepLevels * 2 ? (byte)2 : (byte)1;
                    acc.n++;
                    if (cls == 2) acc.v++;
                    acc.h[(int)Math.Clamp(noise * histScale, 0, 2047)]++;
                    if (classes is not null) classes[(long)y * width + x] = cls;
                }
                return acc;
            },
            acc =>
            {
                lock (gate)
                {
                    blank += acc.b; noisy += acc.n; very += acc.v;
                    for (int i = 0; i < hist.Length; i++) hist[i] += acc.h[i];
                }
            });

        r.BlankPixels = blank;
        r.Noisy = noisy;
        r.VeryNoisy = very;
        if (noisy > 0)
        {
            long run = 0, want = (noisy + 1) / 2;
            for (int i = 0; i < hist.Length; i++)
            {
                run += hist[i];
                if (run >= want) { r.MedianNoiseLevels = i / histScale; break; }
            }
            r.MedianNoiseMicrons = r.MedianNoiseLevels / maxValue * targetDepthMm * 1000.0;
        }
        r.Classes = classes;
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    private static double L(ushort[] p, long i, int w)
        => p[i - w - 1] - 2.0 * p[i - w] + p[i - w + 1] - 2.0 * p[i - 1] + 4.0 * p[i] - 2.0 * p[i + 1] + p[i + w - 1] - 2.0 * p[i + w] + p[i + w + 1];

    private static double L(float[] p, long i, int w)
        => p[i - w - 1] - 2.0 * p[i - w] + p[i - w + 1] - 2.0 * p[i - 1] + 4.0 * p[i] - 2.0 * p[i + 1] + p[i + w - 1] - 2.0 * p[i + w] + p[i + w + 1];

    /// <summary>Median of the first <paramref name="n"/> values, by quickselect; reorders them.</summary>
    private static double Median(float[] a, int n)
    {
        int k = n / 2, lo = 0, hi = n - 1;
        while (lo < hi)
        {
            float pivot = a[(lo + hi) >> 1];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j) { (a[i], a[j]) = (a[j], a[i]); i++; j--; }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else break;
        }
        return a[k];
    }
}
