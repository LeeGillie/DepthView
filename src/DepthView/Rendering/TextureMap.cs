using System;
using System.IO;
using DepthView.Imaging;

namespace DepthView.Rendering;

/// <summary>
/// A surface texture sampled in workpiece space, so it stays locked to the material as you
/// zoom and pan rather than sliding across the screen.
///
/// Two quite different jobs, and the distinction matters:
///
///   Albedo    - the colour of the surface. This is what a photo of oak or slate gives you.
///   Micro     - a fine height field perturbing the surface normal underneath the relief.
///               This is what actually makes brushed metal look brushed: the effect is a
///               stretched, broken-up highlight that moves with the light, not a colour.
///               Loading a photo of brushed brass as an albedo map alone would look flat.
/// </summary>
public sealed class TextureMap
{
    public int Width, Height;
    public float[] R = Array.Empty<float>();
    public float[] G = Array.Empty<float>();
    public float[] B = Array.Empty<float>();
    public float[] Lum = Array.Empty<float>();
    public string? SourcePath;
    public string Label = "";

    private const int MaxEdge = 2048;

    public static TextureMap FromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (img, _) = ImageLoader.Load(bytes, Path.GetFileName(path), path, "material texture");

        int step = 1;
        while (Math.Max(img.Width, img.Height) / step > MaxEdge) step++;

        int w = Math.Max(1, img.Width / step);
        int h = Math.Max(1, img.Height / step);

        var t = new TextureMap
        {
            Width = w,
            Height = h,
            R = new float[(long)w * h],
            G = new float[(long)w * h],
            B = new float[(long)w * h],
            Lum = new float[(long)w * h],
            SourcePath = path,
            Label = Path.GetFileName(path)
        };

        int ch = img.Channels;
        double maxv = img.Kind == SampleKind.Float ? 1.0 : img.MaxValue;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double sr = 0, sg = 0, sb = 0;
                int n = 0;

                for (int dy = 0; dy < step; dy++)
                {
                    int sy = y * step + dy;
                    if (sy >= img.Height) break;
                    for (int dx = 0; dx < step; dx++)
                    {
                        int sx = x * step + dx;
                        if (sx >= img.Width) break;
                        long o = ((long)sy * img.Width + sx) * ch;

                        double a, b2, c;
                        if (img.Kind == SampleKind.Float)
                        {
                            a = img.Floats![o];
                            b2 = ch >= 3 ? img.Floats[o + 1] : a;
                            c = ch >= 3 ? img.Floats[o + 2] : a;
                        }
                        else
                        {
                            a = img.Samples![o];
                            b2 = ch >= 3 ? img.Samples[o + 1] : a;
                            c = ch >= 3 ? img.Samples[o + 2] : a;
                        }

                        sr += a; sg += b2; sb += c;
                        n++;
                    }
                }

                if (n == 0) n = 1;
                long d = (long)y * w + x;

                // Photographs are display-gamma encoded; shading maths wants linear.
                t.R[d] = (float)Math.Pow(Math.Clamp(sr / n / maxv, 0, 1), 2.2);
                t.G[d] = (float)Math.Pow(Math.Clamp(sg / n / maxv, 0, 1), 2.2);
                t.B[d] = (float)Math.Pow(Math.Clamp(sb / n / maxv, 0, 1), 2.2);
                t.Lum[d] = (float)Math.Clamp((sr + sg + sb) / (3.0 * n * maxv), 0, 1);
            }
        }

        return t;
    }

    /// <summary>
    /// Anisotropic noise standing in for a brushed finish. Real brushed metal is scratches
    /// running along one axis: nearly constant along a scratch, changing fast across it.
    /// Useful because a seamless photograph of brushed brass is annoying to come by.
    /// </summary>
    public static TextureMap Brushed(int size = 1024, int seed = 12345)
    {
        var t = new TextureMap
        {
            Width = size,
            Height = size,
            Lum = new float[(long)size * size],
            R = new float[(long)size * size],
            G = new float[(long)size * size],
            B = new float[(long)size * size],
            Label = "procedural brushed"
        };

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Slow along x (following the scratch), fast along y (across it).
                double v = 0.58 * Noise(x / 150.0, y / 3.1, seed)
                         + 0.30 * Noise(x / 52.0, y / 1.45, seed + 7)
                         + 0.12 * Noise(x / 18.0, y / 0.72, seed + 19);

                long d = (long)y * size + x;
                float f = (float)Math.Clamp(v, 0, 1);
                t.Lum[d] = f;
                t.R[d] = t.G[d] = t.B[d] = f;
            }
        }

        return t;
    }

    /// <summary>
    /// Growth rings warped by low frequency noise, plus fine fibres running along the grain.
    /// Tinted from the material's own colour, so maple comes out pale, oak mid and cherry
    /// red-brown without needing three separate photographs.
    /// </summary>
    public static TextureMap Wood(double albedoR, double albedoG, double albedoB,
                                  int size = 1024, int seed = 991)
    {
        var t = Blank(size, "generated wood grain");

        // Grain lines are darker, browner and a little redder than the surrounding wood.
        double darkR = albedoR * 0.40, darkG = albedoG * 0.33, darkB = albedoB * 0.27;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double warp = Noise(x / 210.0, y / 95.0, seed) * 46.0
                            + Noise(x / 58.0, y / 26.0, seed + 3) * 13.0;

                double ring = Math.Sin((y * 0.92 + warp) * 0.115) * 0.5 + 0.5;
                ring = Math.Pow(ring, 2.1);

                // Fibres run along the grain: slow across x, fast across y.
                double fibre = Noise(x / 45.0, y / 1.6, seed + 11);

                double g = Math.Clamp(ring * 0.80 + fibre * 0.20, 0, 1);

                long d = (long)y * size + x;
                t.R[d] = (float)(darkR + (albedoR - darkR) * g);
                t.G[d] = (float)(darkG + (albedoG - darkG) * g);
                t.B[d] = (float)(darkB + (albedoB - darkB) * g);
                t.Lum[d] = (float)g;
            }
        }

        return t;
    }

    /// <summary>Mottled, faintly bedded stone, tinted from the material's own colour.</summary>
    public static TextureMap Speckle(double albedoR, double albedoG, double albedoB,
                                     int size = 1024, int seed = 5150)
    {
        var t = Blank(size, "generated stone speckle");

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double v = 0.52 * Noise(x / 30.0, y / 30.0, seed)
                         + 0.31 * Noise(x / 10.0, y / 10.0, seed + 5)
                         + 0.17 * Noise(x / 3.6, y / 3.6, seed + 9);

                // A faint bedding plane, so it reads as stone rather than plain noise.
                v += Math.Sin((y + Noise(x / 180.0, y / 180.0, seed + 13) * 150.0) * 0.021) * 0.06;
                v = Math.Clamp(v, 0, 1);

                double k = 0.70 + 0.62 * v;
                long d = (long)y * size + x;
                t.R[d] = (float)Math.Clamp(albedoR * k, 0, 1);
                t.G[d] = (float)Math.Clamp(albedoG * k, 0, 1);
                t.B[d] = (float)Math.Clamp(albedoB * k, 0, 1);
                t.Lum[d] = (float)v;
            }
        }

        return t;
    }

    private static TextureMap Blank(int size, string label) => new()
    {
        Width = size,
        Height = size,
        R = new float[(long)size * size],
        G = new float[(long)size * size],
        B = new float[(long)size * size],
        Lum = new float[(long)size * size],
        Label = label
    };

    /// <summary>Stable across runs, unlike string.GetHashCode, so grain does not shuffle.</summary>
    public static int StableSeed(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h & 0x7FFFFFF;
        }
    }

    private static double Noise(double x, double y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);

        double a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed);
        double c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);

        double top = a + (b - a) * fx;
        double bot = c + (d - c) * fx;
        return top + (bot - top) * fy;
    }

    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    /// <summary>Mirrored tiling: folds the coordinate back on itself so seams never show.</summary>
    private static double Fold(double t)
    {
        t = Math.Abs(t) % 2.0;
        return t > 1.0 ? 2.0 - t : t;
    }

    public void Sample(double u, double v, out double r, out double g, out double b)
    {
        double x = Fold(u) * (Width - 1);
        double y = Fold(v) * (Height - 1);

        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double fx = x - x0, fy = y - y0;

        long r0 = (long)y0 * Width, r1 = (long)y1 * Width;

        r = Lerp2(R, r0, r1, x0, x1, fx, fy);
        g = Lerp2(G, r0, r1, x0, x1, fx, fy);
        b = Lerp2(B, r0, r1, x0, x1, fx, fy);
    }

    public double SampleLum(double u, double v)
    {
        double x = Fold(u) * (Width - 1);
        double y = Fold(v) * (Height - 1);

        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        double fx = x - x0, fy = y - y0;

        return Lerp2(Lum, (long)y0 * Width, (long)y1 * Width, x0, x1, fx, fy);
    }

    private static double Lerp2(float[] a, long r0, long r1, int x0, int x1, double fx, double fy)
    {
        double t = a[r0 + x0] + (a[r0 + x1] - a[r0 + x0]) * fx;
        double b = a[r1 + x0] + (a[r1 + x1] - a[r1 + x0]) * fx;
        return t + (b - t) * fy;
    }
}
