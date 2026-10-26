using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DepthView.Ablation;

/// <summary>The program the settings are written for, in its own words and units.</summary>
public enum HostProgram
{
    /// <summary>WeCreat MakeIt, Relief (Emboss) craft: layers, descent per layer, cleaning layer.</summary>
    MakeIt,
    /// <summary>LightBurn, an Image layer in Grayscale mode: power follows the grey, passes with a Z offset each.</summary>
    LightBurnGrayscale,
    /// <summary>LightBurn 3D Slice (or any slicer): one band of levels per pass.</summary>
    LightBurnSlice,
}

/// <summary>Where a line of the sheet comes from. Shown beside every line, so a default is never mistaken for evidence.</summary>
public enum SettingBasis
{
    /// <summary>From the ablation study's evidence at the advice's grade.</summary>
    Evidence,
    /// <summary>A reported recipe with no measured depth behind it (grade E): a place to start, not evidence of a removal.</summary>
    Start,
    /// <summary>A default: not derived from this material or this laser. Said so.</summary>
    Default,
    /// <summary>What the user set: the blank's target depth, the pass count.</summary>
    YourInput,
    /// <summary>How the program works: a mode, a bit depth, a fixed rule.</summary>
    Fixed,
    /// <summary>Nothing in the evidence or the defaults says: find it by test.</summary>
    Unknown,
}

public sealed record SheetLine(string Label, string Value, SettingBasis Basis, string Note)
{
    public string BasisWord => Basis switch
    {
        SettingBasis.Evidence => "evidence",
        SettingBasis.Start => "START (E)",
        SettingBasis.Default => "DEFAULT",
        SettingBasis.YourInput => "yours",
        SettingBasis.Fixed => "fixed",
        _ => "UNKNOWN",
    };
}

/// <summary>
/// The Z the head comes down per layer, with what it rests on. The one figure that is only a
/// guess without an ablation rate: it has to equal what one layer removes, or the focus
/// drifts off the floor. So it is never shown as a bare number - either it is the predicted
/// removal per pass at the advice's grade, or it is a default and says so, or there is none.
/// </summary>
public sealed record ZAdvice(double? Mm, string Basis, string Grade, string Note)
{
    public const string Predicted = "predicted", Default = "default", None = "none";
    public bool IsDefault => Basis == Default;

    /// <summary>WeCreat's own relief jobs step Z this far per layer; what MakeIt is used with when nothing better is known.</summary>
    public const double MakeItDefaultMm = 0.01;

    public static ZAdvice For(SettingsAdvice a)
    {
        if (a.PredictedPerPassUm is { } d)
            return new(System.Math.Round(d / 1000, 4), Predicted, a.Grade,
                $"The predicted removal per pass, so the focus follows the floor: grade {a.Grade}" +
                (a.Grade == "A" ? ", measured on this bench." : ", from other machines - verify on the first coupon."));
        if (a.Recipe is not null)
            return new(MakeItDefaultMm, Default, "E",
                $"A DEFAULT, not derived from {a.MaterialName.ToLowerInvariant()} on this source: there is no ablation-rate " +
                "evidence for the pairing, so nothing predicts what one layer removes. It is the descent WeCreat's own " +
                "relief jobs use (0.01 mm). If a layer really removes more or less than 10 um, the focus drifts off the floor " +
                "and each layer removes less than the last. Cut one coupon of an ablation study and this becomes a measurement.");
        return new(null, None, "E", "No recipe, so no descent: nothing in the evidence applies to this material on this kind of source.");
    }
}

/// <summary>
/// A complete set of settings for a program, from the advice: every line the program asks
/// for, each with what it rests on. The evidence decides power, speed, line pitch, frequency
/// and pulse width; the user decides depth and passes; the program's own rules decide the
/// rest; and what nobody knows is written as unknown rather than filled in.
/// </summary>
public static class RecipeSheet
{
    /// <summary>WeCreat's relief-job cleaning layer, as every MakeIt relief job DepthView has read sets it. A default, not evidence.</summary>
    public const int CleanEveryLayers = 10;
    public const double CleanPowerPercent = 30, CleanSpeedMmPerS = 3500, CleanLineDensityPerCm = 300, CleanFrequencyKHz = 100, CleanPulseWidthNs = 350;

    public static string HostName(HostProgram h) => h switch
    {
        HostProgram.MakeIt => "WeCreat MakeIt (Relief)",
        HostProgram.LightBurnGrayscale => "LightBurn (Image, Grayscale)",
        _ => "LightBurn 3D Slice / a slicer",
    };

    public static List<SheetLine> Build(SettingsAdvice a, HostProgram host)
    {
        var lines = new List<SheetLine>();
        var r = a.Recipe;
        var l = a.Laser;
        var z = ZAdvice.For(a);
        string ev = $"grade {a.Grade}";
        string? evNote = a.Used.Count > 0
            ? $"From {string.Join("; ", a.Used.Select(e => e.Source).Distinct().Take(2))}{(a.Used.Select(e => e.Source).Distinct().Count() > 2 ? " and others" : "")}, {ev}."
            : a.Notes.FirstOrDefault(n => n.StartsWith("Starting recipe")) ?? $"{ev}.";
        // A recipe with no depth behind it is a starting point, and every line of it says so.
        var recipeBasis = a.PredictedPerPassUm is null ? SettingBasis.Start : SettingBasis.Evidence;

        if (r is null)
        {
            lines.Add(new("Recipe", "none", SettingBasis.Unknown, "Nothing in the evidence applies to this material on this kind of source. Cut one coupon of an ablation study."));
            return lines;
        }

        switch (host)
        {
            case HostProgram.MakeIt:
                lines.Add(new("Craft", "Relief (Emboss)", SettingBasis.Fixed, "MakeIt's depth mode: layers that each cut a smaller area, 8-bit, at most 256 layers."));
                lines.Add(new("Image", "the tuned 8-bit map", SettingBasis.Fixed, "Save the tuned PNG for MakeIt from the Tune window (8-bit)."));
                lines.Add(new("Power", $"{r.PowerPercent:0.#} %  ({r.PowerW(l.RatedPowerW):0.#} W of {l.RatedPowerW:0})", recipeBasis, evNote));
                lines.Add(new("Speed", $"{r.SpeedMmPerS:0} mm/s", recipeBasis, evNote));
                lines.Add(new("Line density", $"{r.LineDensityPerCm:0} lines/cm  ({r.LinePitchMm:0.000} mm)", recipeBasis, evNote));
                lines.Add(new("Frequency", $"{r.FrequencyKHz:0} kHz", recipeBasis, evNote));
                if (l.Kind == LaserKind.Mopa) lines.Add(new("Pulse width", $"{r.PulseWidthNs:0} ns", recipeBasis, evNote));
                lines.Add(new("Layers", $"{a.Passes}", SettingBasis.YourInput, $"Your pass count for a {a.TargetDepthMm:0.00} mm target; each layer must then remove {a.NeededPerPassUm:0.#} um." +
                    (a.PassesToTarget is { } pt && pt != a.Passes ? $" At the predicted removal, {pt} layers would reach the target." : "")));
                lines.Add(new("Descent per layer", z.Mm is { } zm ? $"{zm:0.0000} mm" : "not set", z.Basis == ZAdvice.Predicted ? SettingBasis.Evidence : z.Basis == ZAdvice.Default ? SettingBasis.Default : SettingBasis.Unknown, z.Note));
                lines.Add(new("Cleaning layer", $"every {CleanEveryLayers} layers: {CleanPowerPercent:0} %, {CleanSpeedMmPerS:0} mm/s, {CleanLineDensityPerCm:0} lines/cm, {CleanFrequencyKHz:0} kHz" + (l.Kind == LaserKind.Mopa ? $", {CleanPulseWidthNs:0} ns" : ""),
                    SettingBasis.Default, "WeCreat's relief-job cleaning layer, as every MakeIt relief job read so far sets it: a light, fast pass that clears slag from the floor. Not from the evidence for this material. Works from MakeIt 3.0.7; 3.0.6 engraved the previous layer again with no step in Z, so there leave it off."));
                lines.Add(new("Start Z", "the Z found by the guided focus for this lens and blank thickness", SettingBasis.Unknown, "Ablation study > Cut this coupon > Focus records it per laser, lens and thickness; MakeIt's focus assist reports material height, not machine Z."));
                lines.Add(new("Dose", $"{r.DosePerLayerJPerMm2(l.RatedPowerW):0.00} J/mm2 per layer", recipeBasis, "P / (v x h): the quantity removal follows. For checking against the evidence, not a setting."));
                break;

            case HostProgram.LightBurnGrayscale:
                lines.Add(new("Layer type", "Image, mode Grayscale", SettingBasis.Fixed, "Power follows the grey; every pass covers the whole image."));
                lines.Add(new("Image", "the tuned 16-bit map, at true size", SettingBasis.Fixed, "Stamp the blank's size into the file (Tune window) so it imports at scale; import the alignment outline with it."));
                lines.Add(new("Max power", $"{r.PowerPercent:0.#} %  ({r.PowerW(l.RatedPowerW):0.#} W of {l.RatedPowerW:0})", recipeBasis, evNote + " Black in the map runs at this power."));
                lines.Add(new("Min power", "find by test", SettingBasis.Unknown, "The threshold where this material just starts to mark: below it nothing is removed and the top of the relief flattens. Not in the evidence; a short power ladder on scrap gives it."));
                lines.Add(new("Speed", $"{r.SpeedMmPerS:0} mm/s", recipeBasis, evNote));
                lines.Add(new("Line interval", $"{r.LinePitchMm:0.000} mm  ({r.LineDensityPerCm:0} lines/cm)", recipeBasis, evNote));
                lines.Add(new("Frequency", $"{r.FrequencyKHz:0} kHz", recipeBasis, evNote));
                if (l.Kind == LaserKind.Mopa) lines.Add(new("Pulse width", $"{r.PulseWidthNs:0} ns", recipeBasis, evNote));
                lines.Add(new("Passes", $"{a.Passes}", SettingBasis.YourInput, $"Your pass count for a {a.TargetDepthMm:0.00} mm target; at full power each pass must then remove {a.NeededPerPassUm:0.#} um."));
                lines.Add(new("Z offset per pass", z.Mm is { } zg ? $"-{zg:0.0000} mm" : "not set", z.Basis == ZAdvice.Predicted ? SettingBasis.Evidence : z.Basis == ZAdvice.Default ? SettingBasis.Default : SettingBasis.Unknown,
                    z.Note + " In Grayscale every pass cuts every pixel, deep ones fastest, so no one step follows all of them: set it from the removal at full power and keep the total depth modest against the lens's depth of focus" + (a.RayleighMm is { } zr ? $" (about {zr:0.##} mm Rayleigh range, estimated)." : ".")));
                lines.Add(new("Bi-directional, overscan", "on, LightBurn's default", SettingBasis.Fixed, "Program defaults; nothing in the evidence changes them."));
                lines.Add(new("Dither", "none", SettingBasis.Fixed, "Grayscale is the point; dithering would turn depth into dots."));
                break;

            default:
                lines.Add(new("Mode", "3D Slice: one band of levels per pass", SettingBasis.Fixed, "Each pass cuts a smaller area than the last; DepthView's layer figures apply directly."));
                lines.Add(new("Image", "the tuned 16-bit map, at true size", SettingBasis.Fixed, "Stamp the blank's size into the file so it imports at scale."));
                lines.Add(new("Power", $"{r.PowerPercent:0.#} %  ({r.PowerW(l.RatedPowerW):0.#} W of {l.RatedPowerW:0})", recipeBasis, evNote));
                lines.Add(new("Speed", $"{r.SpeedMmPerS:0} mm/s", recipeBasis, evNote));
                lines.Add(new("Line interval", $"{r.LinePitchMm:0.000} mm  ({r.LineDensityPerCm:0} lines/cm)", recipeBasis, evNote));
                lines.Add(new("Frequency", $"{r.FrequencyKHz:0} kHz", recipeBasis, evNote));
                if (l.Kind == LaserKind.Mopa) lines.Add(new("Pulse width", $"{r.PulseWidthNs:0} ns", recipeBasis, evNote));
                lines.Add(new("Passes", $"{a.Passes}", SettingBasis.YourInput, $"Your pass count for a {a.TargetDepthMm:0.00} mm target; each pass must then remove {a.NeededPerPassUm:0.#} um."));
                lines.Add(new("Z step per pass", z.Mm is { } zs ? $"{zs:0.0000} mm" : "not set", z.Basis == ZAdvice.Predicted ? SettingBasis.Evidence : z.Basis == ZAdvice.Default ? SettingBasis.Default : SettingBasis.Unknown, z.Note));
                break;
        }

        string depth = a.PredictedPerPassUm is { } d
            ? $"{d:0.#} um per pass; {a.PredictedTotalMm:0.00} mm in {a.Passes} passes" + (a.PredictedPerPassHighUm - a.PredictedPerPassLowUm > 0.05 ? $" ({a.PredictedTotalLowMm:0.00}-{a.PredictedTotalHighMm:0.00})" : " (one reported figure, no spread)")
            : "not predicted: no depth evidence for this material on this source";
        lines.Add(new("Predicted depth", depth, a.PredictedPerPassUm is null ? SettingBasis.Unknown : SettingBasis.Evidence, a.GradeText));
        return lines;
    }

    /// <summary>The sheet as text for the clipboard: label, value, and what each line rests on.</summary>
    public static string ToText(SettingsAdvice a, HostProgram host)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{HostName(host)} settings for {a.MaterialName} on {a.Laser.Name} ({a.Laser.RatedPowerW:0} W, {a.Laser.Lens}):");
        foreach (var line in Build(a, host))
            sb.AppendLine($"  {line.Label,-20} {line.Value,-44} [{line.BasisWord}]");
        sb.AppendLine();
        sb.AppendLine("What each line rests on:");
        // Lines that share a note (the five settings from one source) are named together.
        var groups = Build(a, host).Where(x => x.Basis != SettingBasis.Fixed).GroupBy(x => x.Note);
        foreach (var g in groups)
            sb.AppendLine($"  {string.Join(", ", g.Select(x => x.Label))}: {g.Key}");
        sb.AppendLine($"  Grade of the evidence: {a.GradeText}");
        return sb.ToString();
    }
}
