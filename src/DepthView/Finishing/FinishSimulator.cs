using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DepthView.Processing;
using DepthView.Rendering;

namespace DepthView.Finishing;

/// <summary>What a finish came to, for the advice panel and the console.</summary>
public sealed class FinishResult
{
    public required FinishLayer Layer { get; init; }
    public List<string> Warnings { get; } = new();

    /// <summary>Of the engraved area: share left dark (cover at least 0.5).</summary>
    public double DarkShareEngraved;
    /// <summary>Of the deeper half of the relief: share left dark.</summary>
    public double DarkShareFloors;
    /// <summary>Of the highs (the top tenth of the relief): share left mostly bare (cover under 0.3).</summary>
    public double BrightShareTop;
    /// <summary>Mean darkness the darkener reached on the engraved area, 0..1.</summary>
    public double MeanDarkness;
    public bool Overdone;
    public double Seconds;
}

/// <summary>
/// Runs a <see cref="FinishRecipe"/> over a height field and returns the surface for the
/// renderer. The model is the one in docs/research/finishing-chemistry.md: patinate
/// everything, then take it back from the highs with a tool described by a probe radius
/// (which recesses it bridges), a reach (how far below that envelope it still touches) and
/// a cut rate. Rough areas react faster and hold their patina harder.
///
/// Every constant here is an estimate (E) unless the catalogue says otherwise. The output
/// is a look, not a prediction.
/// </summary>
public static class FinishSimulator
{
    /// <summary>Engraved below this many millimetres; shallower counts as untouched.</summary>
    private const double EngravedFrom = 0.004, EngravedFull = 0.02;

    /// <summary>Patina removed per minute per unit of cut where a tool fully touches (E).</summary>
    private const double CutPerMinute = 3.0;

    /// <summary>Roughness change per minute of a cleaning burnish (E).</summary>
    private const double BurnishPerMinute = 0.08;

    /// <param name="field">0..1 heights, 1 = the untouched surface.</param>
    /// <param name="mmPerPx">Millimetres per field sample.</param>
    /// <param name="depthMm">Depth that 0 in the field stands for.</param>
    public static FinishResult Run(FinishCatalogue c, FinishRecipe r, float[] field, int w, int h,
                                   double mmPerPx, double depthMm, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = (long)w * h;
        var m = c.Material(r.Material);
        var L = new FinishLayer(w, h);
        (L.MetalR, L.MetalG, L.MetalB) = FinishCatalogue.Linear(m.F0);
        (L.PickleR, L.PickleG, L.PickleB) = FinishCatalogue.Linear(m.PickleTint ?? m.F0);
        var res = new FinishResult { Layer = L };

        depthMm = Math.Max(1e-3, depthMm);
        mmPerPx = Math.Max(1e-6, mmPerPx);

        // Heights in millimetres, 0 at the surface and negative into the work.
        var hmm = new float[n];
        var eng = new float[n];
        for (long i = 0; i < n; i++)
        {
            double d = (1.0 - field[i]) * depthMm;
            hmm[i] = (float)-d;
            eng[i] = (float)Smooth(EngravedFrom, EngravedFull, d);
        }

        // The blank is the circle on the short side. Outside it there is no metal, so it must
        // not hold a tool up: a white corner would otherwise stand above the rim like a wall
        // and keep every tool off the coin's edge. It is dropped to the deepest level inside.
        double bcx = (w - 1) / 2.0, bcy = (h - 1) / 2.0, br = Math.Min(w, h) / 2.0, br2 = br * br;
        float floor = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if ((x - bcx) * (x - bcx) + (y - bcy) * (y - bcy) <= br2) floor = Math.Min(floor, hmm[(long)y * w + x]);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // The canvas border too: the 3D view hangs its slab edge from it, and where the
                // blank touches the border that edge would otherwise be drawn as coin.
                bool border = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                if (!border && (x - bcx) * (x - bcx) + (y - bcy) * (y - bcy) <= br2) continue;
                long i = (long)y * w + x;
                hmm[i] = floor;
                eng[i] = 0;
                L.Void[i] = 1;
            }

        // ---- raw: laser oxide over the engraved area, heavier on the deep floors ----
        var rough = L.MetalRough;
        var oxide = new float[n];
        var (oxR, oxG, oxB) = FinishCatalogue.Linear(m.Oxide);
        for (long i = 0; i < n; i++)
        {
            double e = eng[i];
            rough[i] = (float)Lerp(m.StockRoughness, m.LaserFloorRoughness, e);
            oxide[i] = (float)(e * (0.9 + 0.1 * Math.Clamp(-hmm[i] / depthMm, 0, 1)));
        }

        var dark = new float[n];          // darkness the darkener reached
        var film = new float[n];          // how much of the metal the darkener's film covers
        var patShare = new float[n];      // of the cover, the share that is patina, not oxide
        var dk = c.Darkener(r.Darkener);
        double durability = 1;

        if (r.StopAfter >= FinishStage.Clean)
        {
            var cl = c.Clean(r.Clean);
            for (long i = 0; i < n; i++)
            {
                oxide[i] = (float)(oxide[i] * cl.OxideLeft);
                if (cl.Pickle > 0 && m.PickleTint is not null) L.Pickle[i] = (float)(cl.Pickle * eng[i]);
            }

            if (cl.BurnishTool is { } bt && cl.BurnishMinutes > 0)
            {
                var tool = c.Tool(bt);
                var touch = Contact(tool, 1.0, hmm, w, h, mmPerPx, ct);
                double k = BurnishPerMinute * cl.BurnishMinutes;
                Parallel.For(0, h, y =>
                {
                    for (long i = (long)y * w, e = i + w; i < e; i++)
                        rough[i] = (float)Lerp(rough[i], tool.BareRoughness, 1 - Math.Exp(-k * touch[i]));
                });
            }

            if (cl.AllRoughness is double all)
                Array.Fill(rough, (float)all);
        }
        ct.ThrowIfCancellationRequested();

        if (r.StopAfter >= FinishStage.Polish)
        {
            var pp = c.Pre(r.Prepolish);
            if (pp.Tool is { } pt && pp.Minutes > 0)
            {
                var tool = c.Tool(pt);
                var touch = Contact(tool, 1.0, hmm, w, h, mmPerPx, ct);
                double k = tool.Cut * CutPerMinute * pp.Minutes;
                Parallel.For(0, h, y =>
                {
                    for (long i = (long)y * w, e = i + w; i < e; i++)
                    {
                        double a = 1 - Math.Exp(-k * touch[i]);
                        rough[i] = (float)Lerp(rough[i], tool.BareRoughness, a);
                        oxide[i] = (float)(oxide[i] * (1 - a));
                    }
                });
            }
        }
        ct.ThrowIfCancellationRequested();

        bool darkened = r.StopAfter >= FinishStage.Darken && !dk.IsNone && r.DarkenMinutes > 0;
        double s = Math.Clamp(r.Strength, 0.05, 1);
        double x0 = 0;
        if (darkened)
        {
            // Dilution slows the reaction and moves the colour toward the browns.
            x0 = r.DarkenMinutes / Math.Max(1e-3, dk.TauMinutes) * (0.3 + 0.7 * s);
            if (m.Id == "brass") x0 *= dk.HoldOnBrass;
            res.Overdone = s > 0.6 && x0 > dk.Overdo;
            durability = dk.Durability * (res.Overdone ? 1.6 : 1.0);

            for (long i = 0; i < n; i++)
            {
                // Rough metal has more surface and holds more solution (E); leftover oxide
                // blocks the reaction - darkening needs bare metal (V).
                double boost = 1 + 1.5 * Math.Max(0, rough[i] - 0.3);
                double block = 1 - 0.7 * oxide[i];
                double x = x0 * boost * block;
                dark[i] = (float)(1 - Math.Exp(-x));
                // The film closes over the metal well before it reaches its final colour:
                // a part-darkened coin is evenly brown, not patchy metal (E).
                film[i] = (float)(1 - Math.Exp(-3 * x));
            }
        }

        // Compose the cover: oxide where it remains, patina over the rest.
        double patRoughBase = res.Overdone ? Math.Max(0.85, dk.Roughness) : dk.Roughness;
        Parallel.For(0, h, y =>
        {
            for (long i = (long)y * w, e = i + w; i < e; i++)
            {
                double ox = oxide[i], d = dark[i];
                double pat = film[i] * (1 - ox);
                double cov = ox + pat;
                L.Cover[i] = (float)Math.Clamp(cov, 0, 1);
                double share = cov > 1e-6 ? pat / cov : 0;
                patShare[i] = (float)share;

                double pr = oxR, pg = oxG, pb = oxB;
                if (darkened && share > 0)
                {
                    var (dr, dg, db) = Colour(dk, d, s);
                    pr = Lerp(oxR, dr, share); pg = Lerp(oxG, dg, share); pb = Lerp(oxB, db, share);
                }
                L.PatR[i] = (float)pr; L.PatG[i] = (float)pg; L.PatB[i] = (float)pb;
                L.PatRough[i] = (float)Lerp(0.85, patRoughBase, share);
                L.PatF0[i] = (float)Lerp(0.06, darkened ? dk.F0 : 0.06, share);
            }
        });
        ct.ThrowIfCancellationRequested();

        var before = (float[])L.Cover.Clone();
        var tl = c.Tool(r.Tool);
        bool relieved = r.StopAfter >= FinishStage.Relieve && !tl.IsNone && r.RelieveMinutes > 0;
        if (relieved)
        {
            double p = c.Pressure(r.Pressure).Factor;
            var touch = Contact(tl, p, hmm, w, h, mmPerPx, ct);
            double k = tl.Cut * CutPerMinute * r.RelieveMinutes * p;
            Parallel.For(0, h, y =>
            {
                for (long i = (long)y * w, e = i + w; i < e; i++)
                {
                    double t = touch[i];
                    if (t <= 0) continue;
                    // Rough floors hold patina in their pits (E). Oxide comes off a little
                    // easier than a good patina.
                    double hold = 1.0 / (1.0 + 0.6 * Math.Max(0, rough[i] - 0.3));
                    double dur = Lerp(1.25, durability, patShare[i]);
                    double removed = 1 - Math.Exp(-k * t * hold * dur);
                    L.Cover[i] = (float)(L.Cover[i] * (1 - removed));
                    rough[i] = (float)Lerp(rough[i], tl.BareRoughness, 1 - Math.Exp(-k * t));
                }
            });
        }
        ct.ThrowIfCancellationRequested();

        var se = c.Sealer(r.Sealer);
        if (r.StopAfter >= FinishStage.Seal)
        {
            if (se.Lighten > 0)
            {
                Parallel.For(0, h, y =>
                {
                    for (long i = (long)y * w, e = i + w; i < e; i++)
                    {
                        if (L.Cover[i] <= 0) continue;
                        double share = patShare[i];
                        double lr = L.PatR[i] * (1 + se.Lighten * 2), lg = L.PatG[i] * (1 + se.Lighten * 2), lb = L.PatB[i] * (1 + se.Lighten * 2);
                        if (darkened && share > 0)
                        {
                            // "Back at least one colour": the patina as it was a little earlier.
                            var (dr, dg, db) = Colour(dk, dark[i] * (1 - se.Lighten * 1.25), s);
                            lr = Lerp(lr, dr, share); lg = Lerp(lg, dg, share); lb = Lerp(lb, db, share);
                        }
                        // And a little less saturated.
                        double grey = (lr + lg + lb) / 3;
                        L.PatR[i] = (float)Lerp(lr, grey, se.Lighten * 0.5);
                        L.PatG[i] = (float)Lerp(lg, grey, se.Lighten * 0.5);
                        L.PatB[i] = (float)Lerp(lb, grey, se.Lighten * 0.5);
                    }
                });
            }
            if (se.RoughDelta != 0)
            {
                for (long i = 0; i < n; i++)
                {
                    if (rough[i] > 0.3) rough[i] = (float)Math.Max(0.3, rough[i] + se.RoughDelta);
                    L.PatRough[i] = (float)Math.Clamp(L.PatRough[i] + se.RoughDelta, 0.05, 1);
                }
            }
            L.Clear = se.Clear;
            L.ClearRough = se.ClearRoughness;
        }

        Summarise(c, r, res, field, w, h, eng, dark, before, darkened, relieved);
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    // ------------------------------------------------------------------ the tool model

    /// <summary>
    /// How strongly a tool touches each sample, 0..1. The envelope is a closing of the
    /// surface with a disc the size of the tool, so recesses narrower than the tool are
    /// bridged; contact falls off over the tool's reach below that envelope; and convex
    /// samples (peaks, edges) are touched harder than concave ones.
    /// </summary>
    public static float[] Contact(FinishTool t, double pressure, float[] hmm, int w, int h,
                                  double mmPerPx, CancellationToken ct)
    {
        long n = (long)w * h;
        var touch = new float[n];

        float[]? env = null;
        if (t.RadiusMm is double rmm)
        {
            double rpx = Math.Clamp(rmm / mmPerPx, 1, Math.Max(w, h) / 2.0);
            env = PaddedClose(hmm, w, h, rpx, ct);
        }
        ct.ThrowIfCancellationRequested();

        // Soft tools push further down under pressure; rigid ones do not (E).
        double reach = t.ReachMm >= 0.1 ? t.ReachMm * Math.Sqrt(pressure) : t.ReachMm;
        reach = Math.Max(reach, 0.004);

        double crMm = Math.Min(t.RadiusMm ?? 1.0, 1.0);
        int cr = (int)Math.Clamp(Math.Round(crMm / mmPerPx), 2, 64);
        var blur = BoxBlur(hmm, w, h, cr);
        const double a = 0.02;

        Parallel.For(0, h, y =>
        {
            for (long i = (long)y * w, e = i + w; i < e; i++)
            {
                double top = env is null ? 0 : env[i];
                double delta = hmm[i] - top;            // <= 0
                double wgt = Smooth(-reach, -0.25 * reach, delta);
                if (wgt <= 0) continue;
                double conv = 0.6 + 0.4 * Smooth(-a, a, hmm[i] - blur[i]);
                touch[i] = (float)(wgt * conv);
            }
        });
        return touch;
    }

    /// <summary>
    /// Closing with room around the picture. A tool can overhang the edge of a coin, so the
    /// disc must be free to sit partly off the canvas; padding with the lowest level gives it
    /// that room, where clamping at the border would make it rest on whatever is inside.
    /// </summary>
    private static float[] PaddedClose(float[] src, int w, int h, double rpx, CancellationToken ct)
    {
        int pad = (int)Math.Ceiling(rpx) + 1;
        int pw = w + 2 * pad, ph = h + 2 * pad;
        float low = float.MaxValue;
        foreach (var v in src) if (v < low) low = v;
        var big = new float[(long)pw * ph];
        Array.Fill(big, low);
        for (int y = 0; y < h; y++)
            Array.Copy(src, (long)y * w, big, (long)(y + pad) * pw + pad, w);
        var closed = Morphology.CloseLarge(big, pw, ph, rpx, cancel: ct);
        var env = new float[src.LongLength];
        for (int y = 0; y < h; y++)
            Array.Copy(closed, (long)(y + pad) * pw + pad, env, (long)y * w, w);
        return env;
    }

    /// <summary>Separable box blur with clamped edges.</summary>
    private static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.LongLength];
        var dst = new float[src.LongLength];
        Parallel.For(0, h, y =>
        {
            long row = (long)y * w;
            double sum = 0;
            for (int k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = (float)(sum / (2 * r + 1));
                sum += src[row + Math.Min(w - 1, x + r + 1)] - src[row + Math.Max(0, x - r)];
            }
        });
        Parallel.For(0, w, x =>
        {
            double sum = 0;
            for (int k = -r; k <= r; k++) sum += tmp[(long)Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[(long)y * w + x] = (float)(sum / (2 * r + 1));
                sum += tmp[(long)Math.Min(h - 1, y + r + 1) * w + x] - tmp[(long)Math.Max(0, y - r) * w + x];
            }
        });
        return dst;
    }

    // ------------------------------------------------------------------ colour

    /// <summary>The darkener's colour at darkness d, strength s, in linear RGB.</summary>
    public static (double R, double G, double B) Colour(FinishDarkener dk, double d, double s)
    {
        var a = Stop(dk.Stops, d);
        var b = Stop(dk.DiluteStops.Count == dk.Stops.Count ? dk.DiluteStops : dk.Stops, d);
        // s = 1 is the neat colour; at 0.35 and below it is the dilute browns.
        double t = Smooth(0.2, 0.9, s);
        return (Lerp(b.R, a.R, t), Lerp(b.G, a.G, t), Lerp(b.B, a.B, t));
    }

    private static (double R, double G, double B) Stop(List<string> stops, double d)
    {
        if (stops.Count == 0) return (0.5, 0.5, 0.5);
        if (stops.Count == 1) return FinishCatalogue.Linear(stops[0]);
        double p = Math.Clamp(d, 0, 1) * (stops.Count - 1);
        int i = Math.Min((int)p, stops.Count - 2);
        double f = p - i;
        var a = FinishCatalogue.Linear(stops[i]);
        var b = FinishCatalogue.Linear(stops[i + 1]);
        return (Lerp(a.R, b.R, f), Lerp(a.G, b.G, f), Lerp(a.B, b.B, f));
    }

    // ------------------------------------------------------------------ advice

    private static void Summarise(FinishCatalogue c, FinishRecipe r, FinishResult res, float[] field, int w, int h,
                                  float[] eng, float[] dark, float[] before, bool darkened, bool relieved)
    {
        var L = res.Layer;

        // Measured on the blank only, and against the relief's own range: plenty of maps never
        // reach pure white, so "the untouched surface" is the top tenth of what is there and
        // "the floors" the deeper half.
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, r2 = Math.Min(w, h) * Math.Min(w, h) / 4.0;
        bool Inside(int x, int y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2;
        double hmin = 1, hmax = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (Inside(x, y)) { double v = field[(long)y * w + x]; if (v < hmin) hmin = v; if (v > hmax) hmax = v; }
        double span = Math.Max(1e-6, hmax - hmin);

        long engN = 0, engDark = 0, floorN = 0, floorDark = 0, floorDarkBefore = 0, topN = 0, topBright = 0;
        double darkSum = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!Inside(x, y)) continue;
                long i = (long)y * w + x;
                double cov = L.Cover[i], v = field[i];
                if (eng[i] >= 0.5)
                {
                    engN++;
                    if (cov >= 0.5) engDark++;
                    darkSum += dark[i];
                }
                if (v <= hmin + 0.5 * span)
                {
                    floorN++;
                    if (cov >= 0.5) floorDark++;
                    if (before[i] >= 0.5) floorDarkBefore++;
                }
                if (v >= hmax - 0.1 * span) { topN++; if (cov < 0.3) topBright++; }
            }
        }
        res.DarkShareEngraved = engN == 0 ? 0 : (double)engDark / engN;
        res.DarkShareFloors = floorN == 0 ? 0 : (double)floorDark / floorN;
        res.BrightShareTop = topN == 0 ? 0 : (double)topBright / topN;
        res.MeanDarkness = engN == 0 ? 0 : darkSum / engN;

        var m = c.Material(r.Material);
        var dk = c.Darkener(r.Darkener);
        var cl = c.Clean(r.Clean);
        var W = res.Warnings;

        if (r.StopAfter >= FinishStage.Darken && !dk.IsNone)
        {
            if (cl.OxideLeft > 0.5 && !dk.Film)
                W.Add("Laser oxide is still on the engraving, so the darkener will take unevenly. Clean it off first (ultrasonic or a pin tumbler).");
            if (!dk.Materials.Contains(m.Id))
                W.Add($"{dk.Name} is not a usual choice for {m.Name.ToLowerInvariant()}.");
            if (m.Id == "brass" && dk.HoldOnBrass < 0.5)
                W.Add($"{dk.Name} barely reacts with brass. A selenium black (JAX, Birchwood) is the dependable route.");
            if (res.Overdone)
                W.Add("At this strength and time the black goes crusty and powdery and rubs off when relieved. Dilute to 35-50% or shorten the dip.");
            else if (darkened && !dk.Film && res.MeanDarkness < 0.5)
                W.Add("Light: the recesses will read brown-grey rather than dark. Leave it longer or use it stronger.");
            if (dk.Film)
                W.Add("A thin interference film: its colour shifts toward blue at grazing angles, which this preview does not show.");
        }

        if (relieved)
        {
            double was = floorN == 0 ? 0 : (double)floorDarkBefore / floorN;
            if (darkened && was >= 0.6 && res.DarkShareFloors < 0.6)
                W.Add($"This tool reaches deep: only {res.DarkShareFloors * 100:0}% of the deeper half of the relief keeps its patina. A stiffer tool (hard felt, a flat pad) stays on the highs.");
            if (res.BrightShareTop < 0.6 && darkened)
                W.Add($"Only {res.BrightShareTop * 100:0}% of the highs come back bright. Rub longer, harder, or with a faster-cutting tool.");
        }
        else if (darkened && r.StopAfter >= FinishStage.Relieve && c.Tool(r.Tool).IsNone)
        {
            W.Add("Nothing is relieved, so the highs stay as dark as the recesses and the relief reads only by shading.");
        }

        if (r.StopAfter >= FinishStage.Seal && c.Sealer(r.Sealer).Lighten > 0 && darkened)
            W.Add("Wax takes the patina back about one colour. Some makers darken a little further to allow for it.");
    }

    // ------------------------------------------------------------------ helpers

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Smooth(double e0, double e1, double x)
    {
        if (e1 == e0) return x >= e1 ? 1 : 0;
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
