using System;
using System.Collections.Generic;
using System.Linq;

namespace DepthView.Ablation;

/// <summary>
/// One figure the depth model can stand on: a setting that was cut and read (grade A, this
/// bench) or reported by a source (its grade), with the removal it gave. Everything the
/// advisor says traces back to a list of these, and the grade of the weakest one it used
/// is the grade of the advice.
/// </summary>
public sealed record Evidence
{
    public string StudyName { get; init; } = "";
    public string MaterialName { get; init; } = "";
    public string Alloy { get; init; } = "";
    public LaserSpec Laser { get; init; } = new();
    public PatchSettings Settings { get; init; } = new();
    /// <summary>Mean depth over the patch's layers, µm.</summary>
    public double DepthUm { get; init; }
    /// <summary>Low and high of the readings (or of the reported range), µm.</summary>
    public double DepthLowUm { get; init; }
    public double DepthHighUm { get; init; }
    public string Grade { get; init; } = "E";
    public string Source { get; init; } = "";
    public string Machine { get; init; } = "";
    public bool MeasuredHere { get; init; }

    public double DosePerLayer => Settings.DosePerLayerJPerMm2(Laser.RatedPowerW);
    public double TotalDose => Settings.TotalDoseJPerMm2(Laser.RatedPowerW);
    public int Layers => Math.Max(1, Settings.Layers);
    /// <summary>Removal efficiency, µm per J/mm² of area dose.</summary>
    public double Eta => TotalDose > 0 ? DepthUm / TotalDose : double.NaN;
    public double EtaLow => TotalDose > 0 ? DepthLowUm / TotalDose : double.NaN;
    public double EtaHigh => TotalDose > 0 ? DepthHighUm / TotalDose : double.NaN;
    public double DepthPerLayerUm => DepthUm / Layers;

    /// <summary>A, B, C, D, E as 0..4 (lower is better); "B-derived" counts as B, "B/C" as C.</summary>
    public int GradeRank => Rank(Grade);

    public static int Rank(string grade)
    {
        var g = (grade ?? "E").Trim().ToUpperInvariant();
        if (g.Contains('/')) g = g.Split('/').Last();
        return g.Length > 0 && g[0] is >= 'A' and <= 'E' ? g[0] - 'A' : 4;
    }

    /// <summary>Everything in a database that carries a depth figure, as evidence.</summary>
    public static List<Evidence> Gather(AblationDatabase db)
    {
        var list = new List<Evidence>();
        foreach (var s in db.Studies)
            foreach (var c in s.Coupons.Where(c => !c.Retired))
                foreach (var p in c.Patches)
                {
                    if (p.Measurement is not { MeanDepthUm: { } mean } m || m.ReadingsUm.Count == 0) continue;
                    bool here = p.Status == PatchStatus.Measured && !s.Reference && s.Origin == "local";
                    list.Add(new Evidence
                    {
                        StudyName = s.Name, MaterialName = s.Material.Name, Alloy = s.Material.Alloy, Laser = s.Laser, Settings = p.Settings,
                        DepthUm = mean, DepthLowUm = m.ReadingsUm.Min(), DepthHighUm = m.ReadingsUm.Max(),
                        Grade = here ? "A" : p.Provenance?.Grade ?? (s.Reference ? "E" : "A"),
                        Source = here ? $"measured here: {s.Name}, {c.Name} {p.Label}" : p.Provenance?.Source ?? s.Origin,
                        Machine = here ? s.Laser.Name : p.Provenance?.Machine ?? s.Laser.Name,
                        MeasuredHere = here,
                    });
                }
        return list;
    }
}

/// <summary>
/// The settings the advisor suggests for a job, with what they rest on. Never a bare
/// number: the depth is a band, the grade is the weakest evidence used, and the notes say
/// what was converted, interpolated or refused.
/// </summary>
public sealed record SettingsAdvice
{
    public bool HasRecipe => Recipe is not null;
    /// <summary>The settings for the user's own laser (percent of its rating), in MakeIt's units.</summary>
    public PatchSettings? Recipe { get; init; }
    public LaserSpec Laser { get; init; } = new();
    public string MaterialName { get; init; } = "";
    public double TargetDepthMm { get; init; }
    public int Passes { get; init; }
    /// <summary>Depth each pass must remove to reach the target in the given passes, µm.</summary>
    public double NeededPerPassUm => Passes > 0 ? TargetDepthMm * 1000 / Passes : 0;
    /// <summary>Predicted removal per pass at the recipe, µm - null when the evidence allows no number.</summary>
    public double? PredictedPerPassUm { get; init; }
    public double? PredictedPerPassLowUm { get; init; }
    public double? PredictedPerPassHighUm { get; init; }
    public double? PredictedTotalMm => PredictedPerPassUm is { } d ? d * Passes / 1000 : null;
    public double? PredictedTotalLowMm => PredictedPerPassLowUm is { } d ? d * Passes / 1000 : null;
    public double? PredictedTotalHighMm => PredictedPerPassHighUm is { } d ? d * Passes / 1000 : null;
    /// <summary>Passes that would reach the target at the predicted removal, when the given count does not.</summary>
    public int? PassesToTarget { get; init; }
    /// <summary>MakeIt's descent per layer: the predicted removal per pass, so focus follows the floor. Null without a prediction.</summary>
    public double? ZAdvanceMm => PredictedPerPassUm is { } d ? Math.Round(d / 1000, 4) : null;
    /// <summary>Rayleigh range estimated from the lens's spot (Gaussian, M² = 1), mm - an estimate, not a measurement.</summary>
    public double? RayleighMm { get; init; }
    public string Grade { get; init; } = "E";
    public string Verdict { get; init; } = "";
    public List<string> Notes { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<Evidence> Used { get; init; } = [];

    public string GradeText => Grade switch
    {
        "A" => "A - measured on this bench",
        "B" => "B - peer-reviewed, settings stated, another machine",
        "C" => "C - a vendor's or practitioner's measured figure, another machine",
        "D" => "D - partial settings",
        _ => "E - no measurement behind it",
    };
}

/// <summary>
/// Turns a target depth and pass count into settings, from evidence and nothing else.
///
/// The rules (TODO §7.6/7.9): every number carries its provenance; nothing is extrapolated -
/// the recipe is a tested setting, or lies between two tested settings of the same kind, or
/// is refused; grades never blend - the best grade available for the material and laser
/// kind is used and the rest ignored; and where the evidence has no depth at all (brass,
/// copper, aluminium today) a starting recipe may be offered but no depth is predicted.
///
/// Settings from another rating are carried by watts (<see cref="PowerConversion"/>) and
/// said to be; settings from another lens or another maker's source are said to be too.
/// </summary>
public static class SettingsAdvisor
{
    /// <summary>The depth of focus concern: fibre z_R ≈ π w₀² / λ, with the spot as 2 w₀.</summary>
    public static double? Rayleigh(LaserSpec laser)
    {
        if (!(laser.SpotDiameterUm > 0)) return null;
        double w0 = laser.SpotDiameterUm / 2 * 1e-3, lambda = laser.Kind == LaserKind.Uv ? 355e-6 : 1064e-6;
        return Math.PI * w0 * w0 / lambda;
    }

    public static SettingsAdvice Advise(AblationDatabase db, MaterialSpec material, LaserSpec laser, double targetDepthMm, int passes, string? lens = null)
    {
        passes = Math.Max(1, passes);
        var notes = new List<string>();
        var warn = new List<string>();
        double? zr = Rayleigh(laser);

        // The capability gate first: a source that cannot remove this material gets no
        // recipe, however much evidence of some other pairing there is, and the reason is the
        // verdict. A marginal pairing is advised with the warning in front of it.
        if (Materials.MaterialCatalog.ByAblationName(material.Name) is { } cm)
        {
            var gate = Materials.MaterialCatalog.Gate(Materials.MaterialCatalog.SourceFrom(laser.Kind), cm);
            if (gate.Refused)
                return new SettingsAdvice
                {
                    Laser = laser, MaterialName = material.Name, TargetDepthMm = targetDepthMm, Passes = passes, RayleighMm = zr,
                    Grade = "E", Verdict = $"Refused: {gate.Reason}", Notes = notes, Warnings = warn,
                };
            if (gate.Level == Materials.Capability.Marginal) warn.Add(gate.Reason);
        }

        var all = Evidence.Gather(db);

        // The evidence for this material and kind of laser, best grade only.
        var pool = all.Where(e => e.MaterialName == material.Name && e.Laser.Kind == laser.Kind && e.TotalDose > 0 && e.Eta > 0).ToList();
        if (laser.Kind == LaserKind.Uv || pool.Count == 0)
        {
            // No depth figure at all: offer a starting recipe if a reference holds one, predict nothing.
            // Only a reference study's reported recipe can be a start; a planned patch of a local study is not evidence of anything.
            var starts = db.Studies.Where(s => s.Reference && s.Material.Name == material.Name && s.Laser.Kind == laser.Kind)
                                   .SelectMany(s => s.Coupons.SelectMany(c => c.Patches.Where(p => p.Provenance is not null).Select(p => (s, c, p)))).ToList();
            PatchSettings? recipe = null;
            if (starts.Count > 0)
            {
                var (s, _, p) = starts[0];
                var conv = PowerConversion.Convert(p.Settings, s.Laser.RatedPowerW, laser.RatedPowerW);
                recipe = conv.Settings with { Layers = passes };
                notes.Add($"Starting recipe only: {p.Provenance?.Summary ?? s.Origin} - {p.Reason}. " +
                          (Math.Abs(s.Laser.RatedPowerW - laser.RatedPowerW) > 0.5 ? $"Carried from {s.Laser.RatedPowerW:0} W to {laser.RatedPowerW:0} W by watts ({conv.Note}). " : "") +
                          "No depth was reported for it, so none is predicted.");
            }
            return new SettingsAdvice
            {
                Recipe = recipe, Laser = laser, MaterialName = material.Name, TargetDepthMm = targetDepthMm, Passes = passes, RayleighMm = zr,
                Grade = "E",
                Verdict = $"Uncalibrated: there is no depth figure for {material.Name.ToLowerInvariant()} on a {laser.Kind.ToString().ToUpperInvariant()} source - " +
                          (laser.Kind == LaserKind.Uv ? "no measured UV removal rate exists for any metal. " : "not in the literature, not on this bench. ") +
                          "Cut one coupon of an ablation study on this material and the advice becomes a measurement.",
                Notes = notes, Warnings = warn,
            };
        }
        int best = pool.Min(e => e.GradeRank);
        var used = pool.Where(e => e.GradeRank == best).ToList();
        string grade = ((char)('A' + best)).ToString();
        if (best > 0) notes.Add($"No coupon of {material.Name.ToLowerInvariant()} has been measured on this bench: the advice rests on grade {grade} evidence from other machines ({string.Join("; ", used.Select(e => e.Machine).Distinct())}).");
        var ignored = pool.Where(e => e.GradeRank > best).ToList();
        if (ignored.Count > 0)
            notes.Add($"Grades never blend: {ignored.Count} lower-grade figure{(ignored.Count == 1 ? "" : "s")} ({string.Join("; ", ignored.Select(e => $"{e.Source}, grade {e.Grade}, {e.DepthPerLayerUm:0.#} µm per pass").Distinct())}) exist{(ignored.Count == 1 ? "s" : "")} and {(ignored.Count == 1 ? "was" : "were")} not used.");

        // What each pass must remove, and the tested setting nearest to it (same pulse width and frequency as the tested one).
        double need = targetDepthMm * 1000 / passes;
        var ranked = used.OrderBy(e => Math.Abs(Math.Log(Math.Max(1e-6, e.DepthPerLayerUm)) - Math.Log(Math.Max(1e-6, need)))).ToList();
        var nearest = ranked[0];

        // Between two tested settings of the same pulse width and frequency, speed may be
        // interpolated on dose (d ∝ 1/v at fixed power and pitch). Outside them, never.
        var family = used.Where(e => Math.Abs(e.Settings.PulseWidthNs - nearest.Settings.PulseWidthNs) < 1e-9 && Math.Abs(e.Settings.FrequencyKHz - nearest.Settings.FrequencyKHz) < 1e-9
                                     && e.Laser.Id == nearest.Laser.Id).OrderBy(e => e.DepthPerLayerUm).ToList();
        double lo = family.First().DepthPerLayerUm, hi = family.Last().DepthPerLayerUm;
        PatchSettings chosen; double perPass, perLo, perHi; string how;
        if (family.Count >= 2 && need >= lo && need <= hi)
        {
            // Interpolate the dose per layer between the two neighbours, then set the speed to deliver it.
            var below = family.Last(e => e.DepthPerLayerUm <= need); var above = family.First(e => e.DepthPerLayerUm >= need);
            double t = above.DepthPerLayerUm > below.DepthPerLayerUm ? (need - below.DepthPerLayerUm) / (above.DepthPerLayerUm - below.DepthPerLayerUm) : 0;
            double dose = below.DosePerLayer + t * (above.DosePerLayer - below.DosePerLayer);
            double eta = below.Eta + t * (above.Eta - below.Eta);
            var basis = t < 0.5 ? below : above;
            double pitch = basis.Settings.LinePitchMm;
            double powerW = basis.Settings.PowerW(basis.Laser.RatedPowerW);
            double speed = powerW / (pitch * dose);
            chosen = basis.Settings with { SpeedMmPerS = Math.Round(speed, 0), Layers = passes };
            perPass = need;
            perLo = dose * Math.Min(below.EtaLow, above.EtaLow); perHi = dose * Math.Max(below.EtaHigh, above.EtaHigh);
            how = $"Speed set between two tested settings ({below.Settings.SpeedMmPerS:0} and {above.Settings.SpeedMmPerS:0} mm/s) so that each pass removes about {need:0.#} µm at the tested removal efficiency ({eta:0.##} µm per J/mm²).";
            used = [below, above];
        }
        else
        {
            chosen = nearest.Settings with { Layers = passes };
            perPass = nearest.DepthPerLayerUm; perLo = nearest.DepthLowUm / nearest.Layers; perHi = nearest.DepthHighUm / nearest.Layers;
            how = need < lo
                ? $"Each pass needs only {need:0.#} µm, below the lightest tested setting ({lo:0.#} µm per pass): the lightest is offered as it was tested, not scaled down - no evidence lies below it."
                : need > hi
                    ? $"Each pass needs {need:0.#} µm, above the heaviest tested setting ({hi:0.#} µm per pass): the heaviest is offered as it was tested, not scaled up - no evidence lies above it."
                    : $"The tested setting nearest to {need:0.#} µm per pass, offered as it was tested.";
            used = [nearest];
        }
        notes.Add(how);

        // Carry to the user's rating by watts, and say so.
        var srcLaser = used[0].Laser;
        if (Math.Abs(srcLaser.RatedPowerW - laser.RatedPowerW) > 0.5 || srcLaser.Id != laser.Id)
        {
            var conv = PowerConversion.Convert(chosen, srcLaser.RatedPowerW, laser.RatedPowerW);
            chosen = conv.Settings with { Layers = passes };
            notes.Add($"Tested on {srcLaser.Name}; carried to {laser.Name} by watts - the same watts, pulse energy and frequency, so the physics is the same if percent is linear in power on both ({conv.Note}).");
            if (!conv.Reachable) warn.Add("The tested power is not reachable on this source: the recipe is capped at 100 % and will remove less than predicted.");
            if (!string.Equals(srcLaser.Lens, laser.Lens, StringComparison.OrdinalIgnoreCase))
                notes.Add($"Tested with '{srcLaser.Lens}', not '{laser.Lens}': removal efficiency is calibrated per lens, so the spot and the depth of focus may differ.");
        }
        if (!string.IsNullOrWhiteSpace(lens) && !string.Equals(lens.Trim(), laser.Lens, StringComparison.OrdinalIgnoreCase))
            notes.Add($"The job names the lens '{lens.Trim()}'; the advice assumes '{laser.Lens}'.");

        // Band, passes to target, Z advance, and the warnings the dose supports.
        double totalLo = perLo * passes / 1000, totalHi = perHi * passes / 1000, total = perPass * passes / 1000;
        int? toTarget = perPass > 0 && Math.Abs(total - targetDepthMm) / targetDepthMm > 0.1 ? (int)Math.Round(targetDepthMm * 1000 / perPass) : null;
        double dosePerLayer = chosen.DosePerLayerJPerMm2(laser.RatedPowerW);
        if (material.Name == "Stainless steel" && dosePerLayer > 5.3) warn.Add($"{dosePerLayer:0.#} J/mm² per pass is above the 5.3 J/mm² at which 316L blackened in the literature (grade B): expect a dark, oxidised floor.");
        if (zr is { } z && targetDepthMm > z) warn.Add($"The target depth ({targetDepthMm:0.##} mm) is deeper than the estimated Rayleigh range of this lens ({z:0.##} mm): without a Z advance the floor leaves focus and removal falls off.");
        else if (zr is { } z2) notes.Add($"The target depth is within the estimated Rayleigh range of this lens ({z2:0.##} mm, from the {laser.SpotDiameterUm:0} µm spot): a Z advance matters less here than the pass count.");
        if (laser.SpeedMaxMmPerS > 0 && chosen.SpeedMmPerS > laser.SpeedMaxMmPerS) warn.Add($"{chosen.SpeedMmPerS:0} mm/s is above this source's maximum ({laser.SpeedMaxMmPerS:0}).");
        if (laser.PulseWidthsNs.Count > 0 && !laser.PulseWidthsNs.Any(w => Math.Abs(w - chosen.PulseWidthNs) < 1e-9))
            warn.Add($"{chosen.PulseWidthNs:0} ns is not one of this source's pulse widths ({string.Join(", ", laser.PulseWidthsNs)}): pick the nearest and expect the removal to change - it depends on pulse width strongly.");
        if (laser.FrequencyMinKHz > 0 && (chosen.FrequencyKHz < laser.FrequencyMinKHz || chosen.FrequencyKHz > laser.FrequencyMaxKHz))
            warn.Add($"{chosen.FrequencyKHz:0} kHz is outside this source's {laser.FrequencyMinKHz:0}–{laser.FrequencyMaxKHz:0} kHz.");

        string band = Math.Abs(totalHi - totalLo) > 0.0005 ? $" ({totalLo:0.00}–{totalHi:0.00})" : " (one reported figure, no spread)";
        string verdict = $"Target {targetDepthMm:0.00} mm in {passes} passes; predicted {total:0.00} mm{band}" +
                         (toTarget is { } n ? $" - {(total < targetDepthMm ? "short" : "over")} by {Math.Abs(total - targetDepthMm) / targetDepthMm * 100:0}%; about {n} passes would reach it at this setting." : " - within 10 % of the target.") +
                         $" Grade {grade}.";
        return new SettingsAdvice
        {
            Recipe = chosen, Laser = laser, MaterialName = material.Name, TargetDepthMm = targetDepthMm, Passes = passes,
            PredictedPerPassUm = perPass, PredictedPerPassLowUm = perLo, PredictedPerPassHighUm = perHi, PassesToTarget = toTarget,
            RayleighMm = zr, Grade = grade, Verdict = verdict, Notes = notes, Warnings = warn, Used = used,
        };
    }
}
