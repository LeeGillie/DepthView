using System;
using System.Collections.Generic;

namespace DepthView.Processing;

/// <summary>One small flat top: a peak whose summit is a plateau the slope below it does not explain.</summary>
public sealed record FlatPeak(int Rank, long Pixels, int Level, double CentreX, double CentreY, long BandPixels)
{
    /// <summary>How many times larger the plateau is than the one-level band just below it.</summary>
    public double Ratio => BandPixels == 0 ? double.PositiveInfinity : Pixels / (double)BandPixels;
}

/// <summary>
/// Flattened peaks - the "flat nose tip" (TODO 9.2): small areas perfectly level at the top of a
/// local bump, below pure white, that look like a peak clipped by whatever made the map
/// (an AI depth estimator, an export that ran out of range). A sculptor rebuilds these by hand;
/// DepthView only finds them, outlines them and says how big they are. It offers nothing to "fix".
///
/// Every quantised map has small plateaus at its summits, so flat alone proves nothing. The test
/// is geometry: on a smooth dome each level holds roughly the same area as the one above it (a
/// paraboloid's level bands are equal-area), so a natural summit plateau is about as large as the
/// band one level below it. A clipped peak's plateau is several times larger than that band,
/// because the slope reaches it steeply and then stops. That ratio is what is measured.
/// </summary>
public static class FlatPeaks
{
    /// <summary>Plateau at least this many times its one-level band to count as clipped.</summary>
    public const double RatioLimit = 3.0;

    /// <summary>
    /// Find flattened peaks inside the area <paramref name="inside"/> accepts. Plateaus smaller
    /// than <paramref name="minPixels"/> are ignored (noise), as are plateaus larger than
    /// <paramref name="maxShare"/> of the area, which are flat fields rather than peaks.
    /// </summary>
    public static List<FlatPeak> Find(ushort[] grey, int w, int h, int maxValue, Func<int, int, bool> inside,
                                      int minPixels = 16, double maxShare = 0.01, int limit = 50)
    {
        // Pure white is untouched stock, not a peak; anything below it may be a clipped summit -
        // including the map's own highest level, which is exactly where an estimator clips.
        long area = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (inside(x, y)) area++;
        int top = maxValue;
        long maxPixels = Math.Max(minPixels, (long)(area * maxShare));

        var seen = new bool[grey.Length];
        var found = new List<(long px, int lvl, double cx, double cy, long band)>();
        var queue = new Queue<long>();
        var region = new List<long>();

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                long i = (long)y * w + x;
                if (seen[i] || !inside(x, y)) continue;
                int v = grey[i];
                if (v >= top) { seen[i] = true; continue; }

                // Only start from a pixel no neighbour stands above.
                bool candidate = true;
                for (int dy = -1; dy <= 1 && candidate; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (grey[i + dy * w + dx] > v) { candidate = false; break; }
                if (!candidate) continue;

                // The plateau: 4-connected pixels at exactly this level.
                region.Clear();
                queue.Clear();
                queue.Enqueue(i);
                seen[i] = true;
                bool isTop = true, tooBig = false;
                int below = -1;           // the highest level found just outside it
                double sx = 0, sy = 0;
                while (queue.Count > 0)
                {
                    long p = queue.Dequeue();
                    region.Add(p);
                    if (region.Count > maxPixels) tooBig = true;
                    int px = (int)(p % w), py = (int)(p / w);
                    sx += px; sy += py;
                    if (px == 0 || py == 0 || px == w - 1 || py == h - 1) { isTop = false; continue; }
                    foreach (long q in new[] { p - 1, p + 1, p - w, p + w })
                    {
                        int u = grey[q];
                        if (u == v)
                        {
                            if (!seen[q]) { seen[q] = true; queue.Enqueue(q); }
                        }
                        else if (u > v) isTop = false;
                        else if (u > below) below = u;
                    }
                }
                if (!isTop || tooBig || region.Count < minPixels || below < 0) continue;

                // The band one level below: pixels joined to the plateau, at or above the
                // highest level that borders it.
                long band = 0;
                var bandSeen = new HashSet<long>(region);
                queue.Clear();
                foreach (long p in region) queue.Enqueue(p);
                while (queue.Count > 0 && band <= region.Count * 4L)
                {
                    long p = queue.Dequeue();
                    int px = (int)(p % w), py = (int)(p / w);
                    if (px == 0 || py == 0 || px == w - 1 || py == h - 1) continue;
                    foreach (long q in new[] { p - 1, p + 1, p - w, p + w })
                    {
                        if (bandSeen.Contains(q)) continue;
                        int u = grey[q];
                        if (u >= below && u < v) { bandSeen.Add(q); band++; queue.Enqueue(q); }
                    }
                }

                if (region.Count < RatioLimit * band) continue;

                // A flat top that ends in a wall - lettering, a raised panel - is designed flat,
                // not clipped: three pixels out it has already dropped most of the way to what
                // it stands on. A clipped summit is still on the slope it was cut from there.
                if (DropThreePixelsOut(grey, w, h, region) > 0.05 * maxValue) continue;

                found.Add((region.Count, v, sx / region.Count, sy / region.Count, band));
            }
        }

        found.Sort((a, b) => b.px.CompareTo(a.px));
        var list = new List<FlatPeak>();
        for (int k = 0; k < found.Count && k < limit; k++)
            list.Add(new FlatPeak(k + 1, found[k].px, found[k].lvl, found[k].cx, found[k].cy, found[k].band));
        return list;
    }

    /// <summary>
    /// How far below the plateau the ground is three pixels outside it: the plateau's level
    /// less the median of the third ring of pixels around it (4-connected steps).
    /// </summary>
    private static double DropThreePixelsOut(ushort[] grey, int w, int h, List<long> region)
    {
        var seen = new HashSet<long>(region);
        var ring = new List<long>(region);
        int level = grey[region[0]];
        for (int step = 0; step < 3; step++)
        {
            var next = new List<long>();
            foreach (long p in ring)
            {
                int px = (int)(p % w), py = (int)(p / w);
                if (px == 0 || py == 0 || px == w - 1 || py == h - 1) continue;
                foreach (long q in new[] { p - 1, p + 1, p - w, p + w })
                    if (seen.Add(q)) next.Add(q);
            }
            ring = next;
        }
        if (ring.Count == 0) return 0;
        var levels = new int[ring.Count];
        for (int i = 0; i < ring.Count; i++) levels[i] = grey[ring[i]];
        Array.Sort(levels);
        return level - levels[levels.Length / 2];
    }

    /// <summary>Per-pixel classes for the overlay: 2 on each flattened peak's plateau.</summary>
    public static byte[] Classes(ushort[] grey, int w, int h, IReadOnlyList<FlatPeak> peaks)
    {
        var cls = new byte[grey.Length];
        var queue = new Queue<long>();
        foreach (var pk in peaks)
        {
            long s = (long)Math.Round(pk.CentreY) * w + (long)Math.Round(pk.CentreX);
            // The centroid of a crescent may miss it; walk out to the nearest plateau pixel.
            long start = -1;
            for (int rad = 0; rad < 64 && start < 0; rad++)
                for (int dy = -rad; dy <= rad && start < 0; dy++)
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        long x = (long)Math.Round(pk.CentreX) + dx, y = (long)Math.Round(pk.CentreY) + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        if (grey[y * w + x] == pk.Level) { start = y * w + x; break; }
                    }
            if (start < 0) continue;
            queue.Clear();
            queue.Enqueue(start);
            cls[start] = 2;
            int count = 0;
            while (queue.Count > 0 && count <= pk.Pixels)
            {
                long p = queue.Dequeue();
                count++;
                int px = (int)(p % w), py = (int)(p / w);
                if (px == 0 || py == 0 || px == w - 1 || py == h - 1) continue;
                foreach (long q in new[] { p - 1, p + 1, p - w, p + w })
                    if (cls[q] == 0 && grey[q] == pk.Level) { cls[q] = 2; queue.Enqueue(q); }
            }
        }
        return cls;
    }
}
