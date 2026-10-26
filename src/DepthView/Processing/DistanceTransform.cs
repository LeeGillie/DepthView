using System;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>
/// Exact Euclidean distance transform (Felzenszwalb and Huttenlocher, 2012: lower envelope of
/// parabolas, one pass per axis, O(n)). Used to turn a custom outline - a mask - into the same
/// signed distance a circle or a rounded rectangle gives analytically, so the reserve, the
/// ramp and the preview's boundary line work on any shape without knowing what it is.
/// </summary>
public static class DistanceTransform
{
    /// <summary>
    /// Signed distance in pixels for a mask: positive inside (distance to the nearest outside
    /// pixel), negative outside (minus the distance to the nearest inside pixel). Pixels on
    /// the edge come out at about ±0.5, so "inside" is s > 0.
    /// </summary>
    public static float[] Signed(bool[] inside, int w, int h)
    {
        var toOutside = Distance(inside, w, h, true);
        var toInside = Distance(inside, w, h, false);
        var s = new float[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            long row = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                long i = row + x;
                if (inside[i])
                {
                    // Beyond the canvas is off the blank too: an outline that runs to the
                    // edge of its bounding box (every straight-sided tag does) must still
                    // get its border along that edge, so the distance to the edge counts as
                    // a distance to outside. An edge pixel comes out at +0.5 either way.
                    float edge = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y)) + 1f;
                    s[i] = Math.Min(toOutside[i], edge) - 0.5f;
                }
                else s[i] = -(toInside[i] - 0.5f);
            }
        }
        return s;
    }

    /// <summary>Distance from every pixel to the nearest pixel whose mask value is !target (0 for such pixels themselves).</summary>
    private static float[] Distance(bool[] mask, int w, int h, bool target)
    {
        const float inf = 1e20f;
        var f = new float[(long)w * h];
        for (long i = 0; i < f.Length; i++) f[i] = mask[i] == target ? inf : 0f;

        // Columns, then rows; each 1D pass squares distances, so the result is sqrt'd once.
        Parallel.For(0, w, x =>
        {
            var col = new float[h];
            var d = new float[h];
            var v = new int[h];
            var z = new float[h + 1];
            for (int y = 0; y < h; y++) col[y] = f[(long)y * w + x];
            Edt1D(col, d, v, z, h);
            for (int y = 0; y < h; y++) f[(long)y * w + x] = d[y];
        });
        Parallel.For(0, h, y =>
        {
            var row = new float[w];
            var d = new float[w];
            var v = new int[w];
            var z = new float[w + 1];
            long off = (long)y * w;
            for (int x = 0; x < w; x++) row[x] = f[off + x];
            Edt1D(row, d, v, z, w);
            for (int x = 0; x < w; x++) f[off + x] = MathF.Sqrt(d[x]);
        });
        return f;
    }

    // The 1D lower envelope, as published: v holds the parabola vertices, z the boundaries
    // between them. z[0] is -inf, so k never goes below zero. Doubles for the intersections:
    // the "infinite" input values are 1e20 and the differences would lose their meaning in
    // single precision.
    private static void Edt1D(float[] f, float[] d, int[] v, float[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = Intersect(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersect(f, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = (float)s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    private static double Intersect(float[] f, int q, int p)
        => ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
}
