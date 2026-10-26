using System;
using System.Collections.Generic;

namespace DepthView.Processing;

/// <summary>
/// Iso-lines of a scalar field - the edge of a custom outline at any inset, traced from its
/// distance field so the alignment outline can be written for a shape that has no formula.
/// Classic marching squares on the grid of pixel centres, segments joined into closed loops.
/// Saddle cells are split by the centre value, so two separate features stay separate.
/// </summary>
public static class MarchingSquares
{
    /// <summary>Closed polylines, in pixel coordinates (pixel centres at integer + 0.5), where field crosses level.</summary>
    public static List<List<Pt>> Contours(float[] field, int w, int h, float level)
    {
        // Segments keyed by their endpoints, quantised, so loops can be joined.
        var segments = new List<(Pt A, Pt B)>();
        for (int y = 0; y < h - 1; y++)
        {
            long r0 = (long)y * w, r1 = r0 + w;
            for (int x = 0; x < w - 1; x++)
            {
                float v00 = field[r0 + x], v10 = field[r0 + x + 1], v01 = field[r1 + x], v11 = field[r1 + x + 1];
                int c = (v00 > level ? 1 : 0) | (v10 > level ? 2 : 0) | (v11 > level ? 4 : 0) | (v01 > level ? 8 : 0);
                if (c == 0 || c == 15) continue;
                // Edge crossings: top (00-10), right (10-11), bottom (01-11), left (00-01).
                Pt Top() => new(x + 0.5 + T(v00, v10, level), y + 0.5);
                Pt Right() => new(x + 1.5, y + 0.5 + T(v10, v11, level));
                Pt Bottom() => new(x + 0.5 + T(v01, v11, level), y + 1.5);
                Pt Left() => new(x + 0.5, y + 0.5 + T(v00, v01, level));
                switch (c)
                {
                    case 1: case 14: segments.Add((Left(), Top())); break;
                    case 2: case 13: segments.Add((Top(), Right())); break;
                    case 3: case 12: segments.Add((Left(), Right())); break;
                    case 4: case 11: segments.Add((Right(), Bottom())); break;
                    case 6: case 9: segments.Add((Top(), Bottom())); break;
                    case 7: case 8: segments.Add((Left(), Bottom())); break;
                    case 5:
                    case 10:
                    {
                        float centre = (v00 + v10 + v01 + v11) / 4;
                        bool join = (centre > level) == (c == 5);
                        if (join) { segments.Add((Left(), Top())); segments.Add((Right(), Bottom())); }
                        else { segments.Add((Left(), Bottom())); segments.Add((Top(), Right())); }
                        break;
                    }
                }
            }
        }
        return Join(segments);
    }

    private static double T(float a, float b, float level) => Math.Abs(b - a) < 1e-12 ? 0.5 : Math.Clamp((level - a) / (b - a), 0, 1);

    private static (long, long) Key(Pt p) => ((long)Math.Round(p.X * 256), (long)Math.Round(p.Y * 256));

    private static List<List<Pt>> Join(List<(Pt A, Pt B)> segs)
    {
        var byPoint = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < segs.Count; i++)
        {
            Add(byPoint, Key(segs[i].A), i);
            Add(byPoint, Key(segs[i].B), i);
        }
        var used = new bool[segs.Count];
        var loops = new List<List<Pt>>();
        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var loop = new List<Pt> { segs[i].A, segs[i].B };
            var tail = segs[i].B;
            while (true)
            {
                int next = -1;
                if (byPoint.TryGetValue(Key(tail), out var cands))
                    foreach (int k in cands) if (!used[k]) { next = k; break; }
                if (next < 0) break;
                used[next] = true;
                var (a, b) = segs[next];
                tail = Key(a) == Key(tail) ? b : a;
                loop.Add(tail);
                if (Key(tail) == Key(loop[0])) break;
            }
            if (loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    private static void Add(Dictionary<(long, long), List<int>> d, (long, long) k, int i)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>();
        l.Add(i);
    }

    /// <summary>Ramer-Douglas-Peucker, to keep an SVG of a traced outline small.</summary>
    public static List<Pt> Simplify(List<Pt> pts, double tolerance)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double maxD = 0; int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                double d = Dist(pts[i], pts[s], pts[e]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > tolerance) { keep[idx] = true; stack.Push((s, idx)); stack.Push((idx, e)); }
        }
        var outp = new List<Pt>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) outp.Add(pts[i]);
        return outp;
    }

    private static double Dist(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double px = a.X + t * dx, py = a.Y + t * dy;
        return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
    }
}
