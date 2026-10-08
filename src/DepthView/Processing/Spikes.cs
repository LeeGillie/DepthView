using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Processing;

/// <summary>One isolated pixel far from all eight of its neighbours.</summary>
/// <param name="Delta">Its level minus the nearest neighbour level: negative is a pit (darker,
/// so cut deeper than everything around it), positive a pin (lighter, left standing).</param>
public readonly record struct Spike(int X, int Y, int Level, int Delta);

/// <summary>What <see cref="Spikes.Find"/> found inside the blank.</summary>
public sealed class SpikeReport
{
    public int Width, Height;
    public double BlankRadiusPx;
    /// <summary>A pixel must stand at least this many levels clear of every neighbour.</summary>
    public double MinStepLevels;
    /// <summary>The design's own range, the 0.1-99.9 percentile span inside the blank.</summary>
    public int RangeLevels;
    public long BlankPixels;
    public int Pits, Pins;
    /// <summary>The biggest, by how far they stand clear; up to 50.</summary>
    public List<Spike> Largest = new();
    public double Seconds;

    public int Count => Pits + Pins;
}

/// <summary>
/// Spike detection (TODO 7.4). A spike is one pixel that stands clear of all eight of its
/// neighbours, in the same direction, by more than the neighbours differ among themselves.
/// In a depth map that is not texture: it is a single pass firing where nothing was intended.
/// A dark one is a pit drilled below everything around it; at a high pass count it is cut
/// almost to full depth through a smooth surface. A light one is a pin left standing.
///
/// The threshold is the same as the jagged-edge check's: 4% of the design's range, or three of
/// its own level gaps when that is larger, capped at a quarter of the range - so an 8-bit map
/// saved as 16 bits (steps of 257) is judged on its real steps, and small noise is left to the
/// noise map. Steep slopes are not spikes, because their neighbours already differ by as
/// much as the jump. Measured inside the inscribed circle; inspection only.
/// </summary>
public static class Spikes
{
    /// <summary>More than this many and the finding is a warning rather than a note.</summary>
    public const int WarnCount = 25;

    public static SpikeReport Find(ushort[] grey, int w, int h, int maxValue, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var r = new SpikeReport { Width = w, Height = h, BlankRadiusPx = Math.Min(w, h) / 2.0 };
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double rad = r.BlankRadiusPx - 1.5, rad2 = rad * rad;
        if (w < 3 || h < 3) return r;

        var hist = new long[maxValue + 1];
        long n = 0;
        for (int y = 0; y < h; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy > rad2) continue;
                hist[grey[(long)y * w + x]]++;
                n++;
            }
        }
        r.BlankPixels = n;
        if (n == 0) return r;

        int lo = Percentile(hist, n, 0.001), hi = Percentile(hist, n, 0.999);
        int distinct = 0, prev = -1;
        long gapSum = 0;
        for (int v = lo; v <= hi; v++)
        {
            if (hist[v] == 0) continue;
            if (prev >= 0) gapSum += v - prev;
            prev = v;
            distinct++;
        }
        double quantum = distinct > 1 ? (double)gapSum / (distinct - 1) : 1;
        double range = Math.Max(1, hi - lo);
        r.RangeLevels = (int)range;
        double minStep = Math.Max(0.04 * range, Math.Min(3 * quantum, 0.25 * range));
        r.MinStepLevels = minStep;

        int pits = 0, pins = 0;
        var found = new List<Spike>();
        object gate = new();
        Parallel.For(1, h - 1, () => (0, 0, new List<Spike>()), (y, _, acc) =>
        {
            if (ct.IsCancellationRequested) return acc;
            double dy = y - cy;
            long row = (long)y * w;
            for (int x = 1; x < w - 1; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy > rad2) continue;
                long i = row + x;
                int v = grey[i];
                int nmin = int.MaxValue, nmax = int.MinValue;
                for (int oy = -1; oy <= 1; oy++)
                {
                    long b = i + oy * w;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int u = grey[b + ox];
                        if (u < nmin) nmin = u;
                        if (u > nmax) nmax = u;
                    }
                }
                int spread = nmax - nmin;
                if (v > nmax)
                {
                    int d = v - nmax;
                    if (d >= minStep && spread * 2 <= d) { acc.Item2++; acc.Item3.Add(new Spike(x, y, v, d)); }
                }
                else if (v < nmin)
                {
                    int d = nmin - v;
                    if (d >= minStep && spread * 2 <= d) { acc.Item1++; acc.Item3.Add(new Spike(x, y, v, -d)); }
                }
            }
            // Only the largest 50 are ever reported; keep each thread's list bounded.
            if (acc.Item3.Count > 4096)
            {
                acc.Item3.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
                acc.Item3.RemoveRange(50, acc.Item3.Count - 50);
            }
            return acc;
        }, acc =>
        {
            lock (gate)
            {
                pits += acc.Item1; pins += acc.Item2;
                found.AddRange(acc.Item3);
            }
        });
        ct.ThrowIfCancellationRequested();

        found.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        if (found.Count > 50) found.RemoveRange(50, found.Count - 50);
        r.Pits = pits;
        r.Pins = pins;
        r.Largest = found;
        r.Seconds = sw.Elapsed.TotalSeconds;
        return r;
    }

    /// <summary>
    /// Per-pixel classes for <see cref="ClassOverlay"/>: a small disc around each of the
    /// largest spikes (pits red, pins amber), wide enough to see at preview size.
    /// </summary>
    public static byte[] Classes(int w, int h, SpikeReport r)
    {
        var c = new byte[(long)w * h];
        int rad = Math.Max(2, Math.Min(w, h) / 300);
        foreach (var s in r.Largest)
        {
            byte k = s.Delta < 0 ? (byte)2 : (byte)1;
            for (int dy = -rad; dy <= rad; dy++)
            {
                int y = s.Y + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -rad; dx <= rad; dx++)
                {
                    int x = s.X + dx;
                    if (x < 0 || x >= w || dx * dx + dy * dy > rad * rad) continue;
                    long i = (long)y * w + x;
                    if (c[i] < k) c[i] = k;
                }
            }
        }
        return c;
    }

    /// <summary>
    /// The map dimmed, with a ring around each of the largest spikes - red for a pit, amber for
    /// a pin. A spike is one pixel, far too small to see at preview size, so it is ringed at the
    /// output's own scale: about 1% of the picture across, whatever the map's resolution.
    /// </summary>
    public static byte[] Overlay(ushort[] grey, int w, int h, int maxValue, SpikeReport r,
                                 int outW, int outH, double blankRadiusPx)
    {
        var buf = ClassOverlay.Draw(grey, w, h, maxValue, new byte[(long)w * h], outW, outH, blankRadiusPx);
        int ring = Math.Max(5, Math.Min(outW, outH) / 90);
        double th = Math.Max(1.5, ring / 4.0);
        // Pins first, so a pit that sits beside one is drawn on top.
        foreach (var s in r.Largest.OrderBy(s => s.Delta < 0 ? 1 : 0))
        {
            bool pit = s.Delta < 0;
            byte cr = pit ? (byte)0xE8 : (byte)0xF0, cg = pit ? (byte)0x48 : (byte)0xB4, cb = pit ? (byte)0x4B : (byte)0x5C;
            double ox = (s.X + 0.5) * outW / w, oy = (s.Y + 0.5) * outH / h;
            int x0 = (int)Math.Floor(ox - ring - 1), x1 = (int)Math.Ceiling(ox + ring + 1);
            int y0 = (int)Math.Floor(oy - ring - 1), y1 = (int)Math.Ceiling(oy + ring + 1);
            for (int y = Math.Max(0, y0); y <= Math.Min(outH - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(outW - 1, x1); x++)
                {
                    double d = Math.Sqrt((x + 0.5 - ox) * (x + 0.5 - ox) + (y + 0.5 - oy) * (y + 0.5 - oy));
                    bool onRing = Math.Abs(d - ring) <= th / 2, dot = d <= Math.Max(1.0, th / 2);
                    if (!onRing && !dot) continue;
                    long k = ((long)y * outW + x) * 4;
                    buf[k] = cb; buf[k + 1] = cg; buf[k + 2] = cr; buf[k + 3] = 255;
                }
        }
        return buf;
    }

    private static int Percentile(long[] hist, long n, double p)
    {
        long target = (long)Math.Ceiling(n * p), seen = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            seen += hist[v];
            if (seen >= Math.Max(1, target)) return v;
        }
        return hist.Length - 1;
    }
}
