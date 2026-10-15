using System;
using System.Collections.Generic;
using System.Linq;

namespace DepthView.Ablation;

/// <summary>
/// How a study's settings list is generated. The depth model is <c>d_layer = eta x P/(v x h)</c>
/// (docs/DEPTH-PREDICTION.md): the dose per layer is the quantity removal follows, and the
/// removal efficiency eta is what the study measures, as a function of pulse width, pulse
/// energy (power / frequency) and overlap (speed / frequency against the spot). So the
/// design sweeps dose over decades with a few settings, at the pulse widths of interest,
/// and replicates each so the noise floor is known before anything is interpolated.
/// </summary>
public sealed class DesignOptions
{
    /// <summary>Power levels to test, percent of the rating.</summary>
    public List<double> PowerPercents { get; set; } = [100, 70, 50, 35];
    /// <summary>Speeds to test, mm/s.</summary>
    public List<double> SpeedsMmPerS { get; set; } = [300, 600, 1200, 2400];
    /// <summary>Pulse widths to test, ns (MOPA; ignored on UV).</summary>
    public List<double> PulseWidthsNs { get; set; } = [250];
    public double FrequencyKHz { get; set; } = 45;
    public double LineDensityPerCm { get; set; } = 300;
    public int Layers { get; set; } = 10;
    public double HatchStepDeg { get; set; } = 27;
    /// <summary>How many times each setting is cut (on different coupons when there is room).</summary>
    public int Replicates { get; set; } = 2;
    /// <summary>Settings added by hand, e.g. the exact MakeIt defaults a coin is cut with.</summary>
    public List<PatchSettings> Verification { get; set; } = [];
    /// <summary>Shuffle the cutting order (with a fixed seed, so a study is reproducible), so coupon heating does not become a trend in the data.</summary>
    public bool Randomize { get; set; } = true;
    public int Seed { get; set; } = 20261014;
}

public static class StudyDesign
{
    /// <summary>
    /// The settings list for a laser, with a reason for each. Order is the cutting order.
    /// Settings a laser cannot do (over its speed, a pulse width it lacks) are left out with
    /// their reason in <paramref name="skipped"/>.
    /// </summary>
    public static (List<PatchSettings> Settings, List<string> Reasons) Build(DesignOptions o, LaserSpec laser, out List<string> skipped)
    {
        skipped = [];
        var raw = new List<(PatchSettings s, string why)>();
        var widths = laser.Kind == LaserKind.Mopa ? (o.PulseWidthsNs.Count > 0 ? o.PulseWidthsNs : [250]) : [0.0];
        foreach (double tau in widths)
        {
            if (laser.Kind == LaserKind.Mopa && laser.PulseWidthsNs.Count > 0 && !laser.PulseWidthsNs.Contains(tau))
            {
                skipped.Add($"Pulse width {tau:0} ns: not one the {laser.Name} offers.");
                continue;
            }
            foreach (double p in o.PowerPercents)
                foreach (double v in o.SpeedsMmPerS)
                {
                    if (laser.SpeedMaxMmPerS > 0 && v > laser.SpeedMaxMmPerS) { skipped.Add($"{v:0} mm/s: over the {laser.Name}'s {laser.SpeedMaxMmPerS:0} mm/s."); continue; }
                    var s = new PatchSettings
                    {
                        PowerPercent = Math.Clamp(p, 1, 100), SpeedMmPerS = v, LineDensityPerCm = o.LineDensityPerCm,
                        FrequencyKHz = o.FrequencyKHz, PulseWidthNs = tau, Layers = Math.Max(1, o.Layers), HatchStepDeg = o.HatchStepDeg,
                    };
                    var ph = s.PhysicsOn(laser);
                    raw.Add((s, $"dose {s.DosePerLayerJPerMm2(laser.RatedPowerW):0.00} J/mm² per layer, pulse {ph.PulseEnergyMJ * 1000:0.###} µJ, overlap {ph.PulseOverlap * 100:0}%"));
                }
        }
        foreach (var v in o.Verification)
            raw.Add((v, "verification: a setting used for real work"));

        var all = new List<(PatchSettings s, string why)>();
        for (int r = 0; r < Math.Max(1, o.Replicates); r++)
            all.AddRange(raw.Select(x => (x.s, o.Replicates > 1 ? $"{x.why}; replicate {r + 1} of {o.Replicates}" : x.why)));

        if (o.Randomize)
        {
            var rng = new Random(o.Seed);
            for (int i = all.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (all[i], all[j]) = (all[j], all[i]); }
        }
        return (all.Select(x => x.s).ToList(), all.Select(x => x.why).ToList());
    }

    /// <summary>
    /// Layers that reach about <paramref name="targetDepthUm"/> at a guessed removal rate - a
    /// starting point only, so the first coupon's patches are deep enough to measure well
    /// (150-300 µm against a 12.7 µm graduation) and shallow next to the depth of focus.
    /// </summary>
    public static int SuggestLayers(PatchSettings s, LaserSpec laser, double targetDepthUm = 200, double guessedUmPerJPerMm2 = 2.0)
    {
        double perLayer = s.DosePerLayerJPerMm2(laser.RatedPowerW) * guessedUmPerJPerMm2;
        return perLayer > 0 ? Math.Clamp((int)Math.Round(targetDepthUm / perLayer), 1, 500) : 10;
    }
}

/// <summary>
/// Carrying a setting between the 60 W and 100 W MOPA sources (or any two ratings). The
/// same cut on another rating is the same watts: percent' = percent x rated / rated'. With
/// frequency unchanged the pulse energy and peak power are then the same too, so the
/// physics is the same - IF the percentage is linear in delivered power on both machines
/// (test T1) and the spot is the same lens. A converted setting is therefore a prediction
/// to verify, never a measurement, and is labelled as such on the patch.
/// </summary>
public static class PowerConversion
{
    public sealed record Result(PatchSettings Settings, bool Reachable, string Note);

    public static Result Convert(PatchSettings s, double fromRatedW, double toRatedW)
    {
        if (fromRatedW <= 0 || toRatedW <= 0) return new(s, false, "Unknown rating.");
        double percent = s.PowerPercent * fromRatedW / toRatedW;
        if (percent > 100 + 1e-9)
            return new(s with { PowerPercent = 100 }, false,
                $"Needs {percent:0.#}% of {toRatedW:0} W to match {s.PowerW(fromRatedW):0.#} W: not reachable, capped at 100% ({toRatedW:0.#} W).");
        return new(s with { PowerPercent = Math.Round(percent, 1) }, true,
            $"{s.PowerPercent:0.#}% of {fromRatedW:0} W = {s.PowerW(fromRatedW):0.#} W = {percent:0.#}% of {toRatedW:0} W. Same pulse energy at the same frequency; assumes a linear power scale (test T1).");
    }

    /// <summary>A study's design on another rating, as a new study for that laser; measured results are not carried (they belong to the laser that made them).</summary>
    public static Study ConvertStudy(Study from, LaserSpec to)
    {
        var s = new Study
        {
            Name = $"{from.Name} ({to.RatedPowerW:0} W)", Material = from.Material, Laser = to, Coupon = from.Coupon,
            PatchSizeMm = from.PatchSizeMm, GapMm = from.GapMm, MarginMm = from.MarginMm, IdMark = from.IdMark,
            Notes = $"Converted from \"{from.Name}\" on {from.Laser.Name}: every setting matched by watts. Predictions until cut and measured.",
            DepthBudgetFraction = from.DepthBudgetFraction, AssumedMaxRemovalUmPerJPerMm2 = from.AssumedMaxRemovalUmPerJPerMm2,
        };
        for (int i = 0; i < from.Design.Count; i++)
        {
            var r = Convert(from.Design[i], from.Laser.RatedPowerW, to.RatedPowerW);
            s.Design.Add(r.Settings);
            s.DesignReasons.Add((i < from.DesignReasons.Count ? from.DesignReasons[i] + "; " : "") + r.Note);
        }
        return s;
    }
}
