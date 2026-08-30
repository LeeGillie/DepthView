using System;
using System.Threading.Tasks;

namespace DepthView.Rendering;

/// <summary>
/// The height field plus anything expensive that can be cached across frames.
///
/// Ambient occlusion is the big one. It samples the height field sixteen times per pixel,
/// which was affordable for a fixed top-down view but not once the camera can move: it would
/// be recomputed on every orbit step for no reason, because occlusion is a property of the
/// surface, not of where you are standing. Computing it once at field resolution and
/// sampling that map turns sixteen taps per pixel into one.
/// </summary>
public sealed class ReliefScene
{
    public readonly float[] Field;
    public readonly int W, H;

    private float[]? _ao;
    private double _aoZk = double.NaN, _aoStrength = double.NaN;
    private int _aoSlices = -1;
    private bool _aoInvert;
    private readonly object _lock = new();

    public ReliefScene(float[] field, int w, int h)
    {
        Field = field;
        W = w;
        H = h;
    }

    /// <summary>Height at integer field coordinates, with invert and slicing applied.</summary>
    private double At(int x, int y, bool invert, int slices)
    {
        if (x < 0) x = 0; else if (x >= W) x = W - 1;
        if (y < 0) y = 0; else if (y >= H) y = H - 1;

        double v = Field[(long)y * W + x];
        if (invert) v = 1.0 - v;

        if (slices > 1)
        {
            double n = slices;
            double step = Math.Floor(v * n);
            if (step > n - 1) step = n - 1;
            v = step / (n - 1);
        }

        return v;
    }

    /// <summary>
    /// Occlusion map at field resolution, or null when occlusion is switched off.
    /// Recomputed only when something it actually depends on changes.
    /// </summary>
    public float[]? Occlusion(double zk, double strength, int slices, bool invert)
    {
        if (strength <= 0.001) return null;

        lock (_lock)
        {
            if (_ao is not null
                && Math.Abs(_aoZk - zk) < 1e-9
                && Math.Abs(_aoStrength - strength) < 1e-9
                && _aoSlices == slices
                && _aoInvert == invert)
                return _ao;

            var ao = new float[(long)W * H];

            double r1 = Math.Max(1.5, W / 90.0);
            double r2 = Math.Max(3.0, W / 34.0);

            var dirs = new (double dx, double dy)[8];
            for (int a = 0; a < 8; a++)
            {
                double ang = a * Math.PI / 4.0;
                dirs[a] = (Math.Cos(ang), Math.Sin(ang));
            }

            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    double h = At(x, y, invert, slices);
                    double occ = 0;

                    for (int a = 0; a < 8; a++)
                    {
                        var (dx, dy) = dirs[a];
                        for (int ring = 0; ring < 2; ring++)
                        {
                            double rr = ring == 0 ? r1 : r2;
                            int px = (int)Math.Round(x + dx * rr);
                            int py = (int)Math.Round(y + dy * rr);
                            double slope = (At(px, py, invert, slices) - h) * zk / rr;
                            if (slope > 0) occ += slope;
                        }
                    }

                    occ /= 16.0;
                    ao[(long)y * W + x] = (float)(1.0 / (1.0 + occ * 6.0 * strength));
                }
            });

            _ao = ao;
            _aoZk = zk;
            _aoStrength = strength;
            _aoSlices = slices;
            _aoInvert = invert;
            return ao;
        }
    }
}
