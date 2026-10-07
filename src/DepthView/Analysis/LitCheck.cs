using System;

namespace DepthView.Analysis;

/// <summary>
/// Does a grey image look lit - a shaded picture of a relief - rather than a depth map of it?
/// (TODO 9.1.)
///
/// A depth map records height, and a raised feature reads the same from every side: it rises
/// and falls symmetrically. A lit picture records light: every feature is bright on the side
/// facing the lamp and dark, often shadowed, on the far side, the same way across the whole
/// image. That one-sidedness is what this measures.
///
/// For each of 24 directions u, the difference across a pixel along u, v(x+u) - v(x-u), is
/// correlated with how far the pixel is from the mean, squared. In a depth map that correlation
/// comes out the same in every direction and its first harmonic around the circle is close to
/// zero; in a render with directional light and shadow it swings with the light. Its first
/// harmonic, scaled by the image's own contrast, is the score. Second-order statistics cannot
/// see this - every image's autocorrelation is symmetric - so it has to be third order.
///
/// Measured on Lee's files: grey shaded renders of three coins scored 0.037 to 0.057, genuine
/// depth maps (Blodgett Arch, the wolf, the Huey and FOE Eagle coins, and the map one of the
/// renders was made from) 0.0004 to 0.006. A tilted ramp and a smooth dome score zero. A
/// render of smooth bumps with no shadow scores low and would not be caught: the tell is
/// directional shading with shadow, which is what real renders have. Hence a warning, not a
/// verdict, until it has met many more files.
/// </summary>
public static class LitCheck
{
    /// <summary>Score at or above which the image is reported as looking lit.</summary>
    public const double Threshold = 0.015;

    /// <summary>Working size: the long edge is reduced to this (nearest neighbour) first.</summary>
    private const int Edge = 1024;

    /// <summary>Offset, in working pixels, across which the one-sidedness is measured.</summary>
    private const int Reach = 4;

    /// <summary>The score: 0 for a perfectly symmetric map, a few hundredths for a lit render.</summary>
    public static double Score(ushort[] samples, int channels, int width, int height, int maxValue)
    {
        if (width < 4 * Reach + 8 || height < 4 * Reach + 8 || maxValue <= 0) return 0;

        // The first channel, as everywhere a grey map is read: on a grey map stored as colour
        // all three are the same.
        int ch = Math.Max(1, channels);
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)Edge));
        int w = (width + step - 1) / step, h = (height + step - 1) / step;
        var a = new double[w * h];
        for (int y = 0; y < h; y++)
        {
            long srow = (long)Math.Min(height - 1, y * step) * width;
            for (int x = 0; x < w; x++)
                a[y * w + x] = samples[(srow + Math.Min(width - 1, x * step)) * ch] / (double)maxValue;
        }

        int d = Reach;
        double mean = 0;
        long count = 0;
        for (int y = d; y < h - d; y++)
            for (int x = d; x < w - d; x++) { mean += a[y * w + x]; count++; }
        if (count == 0) return 0;
        mean /= count;

        double scale = 0;
        for (int y = d; y < h - d; y++)
            for (int x = d; x < w - d; x++) { double v = a[y * w + x] - mean; scale += Math.Abs(v * v * v); }
        scale /= count;
        if (scale < 1e-12) return 0;

        // Covariance of the difference across each pixel with its squared distance from the
        // mean. The mean difference is taken out first, so a ramp - a constant difference
        // everywhere - scores nothing.
        const int dirs = 24;
        double re = 0, im = 0;
        for (int k = 0; k < dirs; k++)
        {
            double t = 2 * Math.PI * k / dirs;
            int dx = (int)Math.Round(d * Math.Cos(t)), dy = (int)Math.Round(d * Math.Sin(t));
            double sf = 0, sv2 = 0, sfv2 = 0;
            for (int y = d; y < h - d; y++)
            {
                int row = y * w;
                for (int x = d; x < w - d; x++)
                {
                    double f = a[(y + dy) * w + x + dx] - a[(y - dy) * w + x - dx];
                    double v = a[row + x] - mean, v2 = v * v;
                    sf += f; sv2 += v2; sfv2 += f * v2;
                }
            }
            double cov = sfv2 / count - (sf / count) * (sv2 / count);
            re += cov * Math.Cos(t);
            im -= cov * Math.Sin(t);
        }
        double amp = 2 * Math.Sqrt(re * re + im * im) / dirs;
        return amp / scale;
    }
}
