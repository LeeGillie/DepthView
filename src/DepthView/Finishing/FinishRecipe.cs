using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DepthView.Finishing;

/// <summary>The steps of a finish, in the order they are done.</summary>
public enum FinishStage { Raw, Clean, Polish, Darken, Relieve, Seal }

/// <summary>
/// What someone will do to the coin after the laser: one choice per step, by catalogue id.
/// Plain data, so it can be written into a job report or a .dvp profile and read back.
/// </summary>
public sealed class FinishRecipe
{
    public string Material { get; set; } = "brass";
    public string Clean { get; set; } = "ultrasonic";
    public string Prepolish { get; set; } = "none";

    public string Darkener { get; set; } = "jax_black";
    /// <summary>0..1, where 1 is the product neat.</summary>
    public double Strength { get; set; } = 0.5;
    public double DarkenMinutes { get; set; } = 1;

    public string Tool { get; set; } = "propad";
    public string Pressure { get; set; } = "medium";
    public double RelieveMinutes { get; set; } = 2;

    public string Sealer { get; set; } = "wax";

    /// <summary>Show the coin as it is after this step.</summary>
    public FinishStage StopAfter { get; set; } = FinishStage.Seal;

    public FinishRecipe Clone() => (FinishRecipe)MemberwiseClone();

    /// <summary>Pick up the product's own defaults when the darkener changes.</summary>
    public void UseDarkenerDefaults(FinishDarkener d)
    {
        Darkener = d.Id;
        Strength = d.DefaultStrength;
        DarkenMinutes = d.DefaultMinutes;
    }

    /// <summary>
    /// Reads "key=value" pairs separated by ';' or ',' - the --finish option of --render.
    /// Unknown keys are reported, not ignored, so a typo does not silently render the default.
    /// </summary>
    public static FinishRecipe Parse(string spec, out List<string> problems)
    {
        var r = new FinishRecipe();
        problems = new List<string>();
        foreach (var part in spec.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) { problems.Add($"'{part}' is not key=value"); continue; }
            string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
            double D() => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN;

            switch (k)
            {
                case "material": r.Material = v; break;
                case "clean": r.Clean = v; break;
                case "prepolish": case "polish": r.Prepolish = v; break;
                case "darken": case "darkener": r.Darkener = v; break;
                case "strength": if (!double.IsNaN(D())) r.Strength = Math.Clamp(D(), 0.05, 1); break;
                case "minutes": case "darkenminutes": if (!double.IsNaN(D())) r.DarkenMinutes = Math.Max(0, D()); break;
                case "relieve": case "tool": r.Tool = v; break;
                case "pressure": r.Pressure = v; break;
                case "relieveminutes": case "rubminutes": if (!double.IsNaN(D())) r.RelieveMinutes = Math.Max(0, D()); break;
                case "seal": case "sealer": r.Sealer = v; break;
                case "stage": case "stop":
                    if (Enum.TryParse<FinishStage>(v, true, out var st)) r.StopAfter = st;
                    else problems.Add($"stage '{v}' is not one of {string.Join(", ", Enum.GetNames<FinishStage>())}");
                    break;
                default: problems.Add($"unknown key '{kv[0]}'"); break;
            }
        }
        return r;
    }

    /// <summary>The recipe in words, with makers, for a report or the console.</summary>
    public string Describe(FinishCatalogue c)
    {
        var sb = new StringBuilder();
        var m = c.Material(Material);
        sb.AppendLine($"Material      {m.Name}");
        sb.AppendLine($"Clean         {c.Clean(Clean).Name}");
        if (StopAfter >= FinishStage.Polish) sb.AppendLine($"Pre-polish    {c.Pre(Prepolish).Name}");
        if (StopAfter >= FinishStage.Darken)
        {
            var d = c.Darkener(Darkener);
            sb.AppendLine(d.IsNone
                ? $"Darken        {d.Name}"
                : $"Darken        {d.Name} ({d.Maker}), {Strength * 100:0}% strength, {Minutes(DarkenMinutes)}");
        }
        if (StopAfter >= FinishStage.Relieve)
        {
            var t = c.Tool(Tool);
            sb.AppendLine(t.IsNone
                ? $"Relieve       {t.Name}"
                : $"Relieve       {t.Name}, {c.Pressure(Pressure).Name.ToLowerInvariant()} pressure, {Minutes(RelieveMinutes)}");
        }
        if (StopAfter >= FinishStage.Seal) sb.AppendLine($"Seal          {c.Sealer(Sealer).Name}");
        return sb.ToString().TrimEnd();
    }

    public static string Minutes(double m)
        => m < 1 ? $"{m * 60:0} s"
         : m >= 120 ? $"{m / 60:0.#} h"
         : $"{m:0.#} min";
}
