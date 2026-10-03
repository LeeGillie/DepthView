using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DepthView.Processing;

/// <summary>What will turn the map into a job. Decides the defaults, never the measurements.</summary>
public enum WizardTarget
{
    /// <summary>WeCreat MakeIt, Relief (Emboss): 8 bits, at most 256 layers, Z 0.01 mm a layer.</summary>
    MakeIt,

    /// <summary>LightBurn on a G-code machine: an Image layer in Grayscale mode, no slicing.</summary>
    LightBurn,

    /// <summary>A slicer that cuts one band of levels per pass: LightBurn 3D Slice on a galvo, and the like.</summary>
    Slicer,
}

/// <summary>The image's background: something around the design, or part of it.</summary>
public enum BackgroundRole { Surround, Floor }

/// <summary>The rim the blank's edge gets.</summary>
public enum RimChoice
{
    /// <summary>Add ours and size the blank so the artwork's own drawn rim lands under it.</summary>
    Replace,

    /// <summary>Add ours.</summary>
    Add,

    /// <summary>No rim: the map is engraved to its edges.</summary>
    None,
}

/// <summary>Where the blank goes on the canvas, when there is a rim to place.</summary>
public enum PlacementChoice
{
    /// <summary>Centre the blank on the design and size it to the design (FitPolicy.Design).</summary>
    Design,

    /// <summary>Keep the canvas centre; grow the canvas until the design clears the rim (FitPolicy.Content).</summary>
    Grow,

    /// <summary>Keep the canvas as it is; the rim paints over anything beyond it.</summary>
    AsIs,
}

/// <summary>How a level point is chosen.</summary>
public enum LevelChoice
{
    /// <summary>From the floor (or flat top) DepthView measured, so it becomes one exact level.</summary>
    Feature,

    /// <summary>The extreme 0.1% of the design: what Suggest has always done.</summary>
    Percentile,

    /// <summary>The design's very lowest (or highest) level: nothing clipped.</summary>
    Extreme,

    /// <summary>A level the user dragged to.</summary>
    Custom,
}

/// <summary>Which nearly level areas the wizard offers to change.</summary>
public enum FlatScope { None, Floor, All }

/// <summary>
/// The tuning wizard's answers. Every field is a decision the user made, or the default they
/// accepted; the levels themselves are worked out from these and the survey, so changing an
/// earlier answer (is the background part of the design?) moves the level points with it.
/// </summary>
public sealed class WizardAnswers
{
    public WizardTarget Target = WizardTarget.MakeIt;
    public BackgroundRole Background = BackgroundRole.Surround;
    public RimChoice Rim = RimChoice.Add;
    public double RimMm = 1.0;
    public PlacementChoice Placement = PlacementChoice.Design;
    public double RampMm = 0.30;
    public LevelChoice Floor = LevelChoice.Feature;
    public int BlackCustom;
    public LevelChoice Top = LevelChoice.Feature;
    public int WhiteCustom;
    public FlatScope FlatScope = FlatScope.Floor;
    public FlatMode[] FlatModes = Array.Empty<FlatMode>();
    public int Passes = 72;
    public int Bits = 8;
    public bool WriteDpi = true;
    public bool Outline;
    public string Suffix = "-tuned-makeit";

    public WizardAnswers Clone()
    {
        var c = (WizardAnswers)MemberwiseClone();
        c.FlatModes = (FlatMode[])FlatModes.Clone();
        return c;
    }
}

/// <summary>
/// What the wizard recommends, and why, in one place - so the window, the tests and the
/// tuning guide cannot drift apart. Each recommendation is a reading of a measurement; none
/// of them is a fact about the user's intent, which is why each one is a question.
/// </summary>
public static class WizardAdvice
{
    /// <summary>MakeIt 3.0.6 lowered Z this far per layer in every relief job DepthView has read.</summary>
    public const double MakeItZStepMm = 0.01;

    /// <summary>MakeIt works in 8 bits: up to 256 layers.</summary>
    public const int MakeItMaxLayers = 256;

    public static double RampMm(WizardTarget t) => t == WizardTarget.MakeIt ? 0.30 : 0.20;
    public static int Bits(WizardTarget t) => t == WizardTarget.MakeIt ? 8 : 16;
    public static bool Outline(WizardTarget t) => t == WizardTarget.LightBurn;

    public static string Suffix(WizardTarget t) => t switch
    {
        WizardTarget.MakeIt => "-tuned-makeit",
        WizardTarget.LightBurn => "-tuned-lightburn",
        _ => "-tuned",
    };

    /// <summary>
    /// MakeIt: layers x 0.01 mm = depth, so the focus follows the floor down. The others:
    /// 256, which for LightBurn grayscale is only the count the figures are quoted against.
    /// </summary>
    public static int Passes(WizardTarget t, double depthMm) => t == WizardTarget.MakeIt
        ? Math.Clamp((int)Math.Round(depthMm / MakeItZStepMm), 2, MakeItMaxLayers)
        : 256;

    /// <summary>A target's defaults, applied when the target is chosen.</summary>
    public static void ApplyTarget(WizardAnswers a, WizardTarget t, double depthMm)
    {
        a.Target = t;
        a.RampMm = RampMm(t);
        a.Bits = Bits(t);
        a.Outline = Outline(t);
        a.WriteDpi = true;
        a.Suffix = Suffix(t);
        a.Passes = Passes(t, depthMm);
    }

    public static BackgroundRole Background(DesignSurvey s)
        => s.BackgroundLooksLikeFloor ? BackgroundRole.Floor : BackgroundRole.Surround;

    public static RimChoice Rim(DesignSurvey s) => s.DrawnRim is not null ? RimChoice.Replace : RimChoice.Add;

    /// <summary>Off centre by half a millimetre or more, or sitting on a surround: centre on the design.</summary>
    public static PlacementChoice Placement(DesignSurvey s, BackgroundRole bg, double blankMm)
    {
        double offMm = OffCentreMm(s, blankMm);
        bool surround = bg == BackgroundRole.Surround && s.BackgroundShare >= 0.10;
        return offMm >= DepthTuner.OffCentreNoteMm || surround ? PlacementChoice.Design : PlacementChoice.Grow;
    }

    public static double OffCentreMm(DesignSurvey s, double blankMm)
        => blankMm > 0 ? s.OffCentrePx / (Math.Min(s.Width, s.Height) / blankMm) : 0;

    /// <summary>A floor or flat top, or a detached tail across an empty gap: use it. Otherwise the percentile.</summary>
    public static LevelChoice Level(Extreme e) => e.Found || e.Source == "gap" ? LevelChoice.Feature : LevelChoice.Percentile;

    /// <summary>
    /// What to do with one nearly level area, and the reason in a sentence. The rules: an area
    /// the level points already make one depth needs nothing; jitter that straddles a layer
    /// boundary engraves as speckle, so it is flattened; jitter riding on a real slope is
    /// smoothed when it is big enough to matter against a layer; a slope on its own is the
    /// design, and its contour lines are meant.
    /// </summary>
    public static (FlatMode Mode, string Why) Flat(FlatArea a, int black, int white, int passes, int maxValue)
    {
        if (a.High <= black)
            return (FlatMode.Leave, "The black point already makes all of it one depth.");
        if (a.Low >= white)
            return (FlatMode.Leave, "The white point already leaves all of it untouched.");

        int crosses = a.BoundariesCrossed(passes, black, white, maxValue);
        double layer = (white - black) / (double)Math.Max(1, passes - 1);

        if (a.MostlyJitter)
            return crosses >= 1
                ? (FlatMode.Flatten, $"Its spread is noise, and it straddles {Plural(crosses, "layer boundary", "layer boundaries")} - "
                                   + "it would engrave speckled. Flattening makes it one layer.")
                : (FlatMode.Leave, "It already falls inside one layer, so it will engrave level as it is.");

        if (crosses >= 1 && a.Jitter * 2 >= layer * 0.10)
            return (FlatMode.Smooth, "A real slope with noise on it. Smoothing keeps the slope and removes the speckle "
                                   + "where layer boundaries cross it.");

        return (FlatMode.Leave, "A real slope or dish in the design, with little noise. The layer boundaries "
                              + "crossing it are its contour lines.");
    }

    public static string Plural(long n, string one, string many)
        => n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? one : many);
}

public sealed record WizardChange(string What, string Why);

/// <summary>
/// The wizard's answers turned into settings: the options for <see cref="DepthTuner.Apply"/>,
/// every change with its reason, and the command line that makes the same file.
/// </summary>
public sealed class WizardPlan
{
    public TuningOptions Options = new();
    public int Black, White, Passes;
    public bool BackgroundIsDesign, RimCovered, Outline;
    public List<WizardChange> Changes = new();
    public string Command = "";
    public string SuggestedName = "";

    public static (int Black, int White) Levels(DesignSurvey s, WizardAnswers a)
    {
        bool bg = a.Background == BackgroundRole.Floor;
        bool rim = a.Rim == RimChoice.Replace && s.DrawnRim is not null;
        var floor = s.Floor(bg, rim);
        var top = s.Top(bg, rim);

        int black = a.Floor switch
        {
            LevelChoice.Feature when floor.Found || floor.Source == "gap" => floor.Suggested,
            LevelChoice.Extreme => s.PercentileLevel(0, bg, rim),
            LevelChoice.Custom => a.BlackCustom,
            _ => s.PercentileLevel(0.001, bg, rim),
        };
        int white = a.Top switch
        {
            LevelChoice.Feature when top.Found || top.Source == "gap" => top.Suggested,
            LevelChoice.Extreme => s.PercentileLevel(1, bg, rim),
            LevelChoice.Custom => a.WhiteCustom,
            _ => s.PercentileLevel(0.999, bg, rim),
        };
        black = Math.Clamp(black, 0, s.MaxValue - 1);
        white = Math.Clamp(white, black + 1, s.MaxValue);
        return (black, white);
    }

    /// <summary>Is a flat area offered at this scope?</summary>
    public static bool InScope(FlatArea area, FlatScope scope)
        => scope == FlatScope.All || (scope == FlatScope.Floor && area.TouchesFloor);

    /// <summary>
    /// Build the settings. <paramref name="width"/> and <paramref name="height"/> are the full
    /// image's: millimetres are resolved against it, exactly as the Tune window does, and a
    /// preview scales the rim from there.
    /// </summary>
    public static WizardPlan Build(DesignSurvey s, WizardAnswers a, string fileName,
                                   double blankMm, double depthMm)
    {
        var p = new WizardPlan
        {
            BackgroundIsDesign = a.Background == BackgroundRole.Floor,
            RimCovered = a.Rim == RimChoice.Replace && s.DrawnRim is not null,
            Passes = Math.Max(2, a.Passes),
            Outline = a.Outline,
        };
        (p.Black, p.White) = Levels(s, a);
        var inv = CultureInfo.InvariantCulture;
        string N(double v, string f = "0.##") => v.ToString(f, inv);

        var o = new TuningOptions
        {
            BlackPoint = p.Black,
            WhitePoint = p.White,
            Stretch = true,
            OutputBitDepth = a.Bits == 8 ? 8 : 16,
            BlankDiameterMm = blankMm,
            TargetDepthMm = depthMm,
        };
        if (a.Rim != RimChoice.None)
        {
            o.RimWidthMm = a.RimMm;
            o.RimRampMm = a.RampMm;
            o.Fit = a.Rim == RimChoice.Replace ? FitPolicy.Design : a.Placement switch
            {
                PlacementChoice.Design => FitPolicy.Design,
                PlacementChoice.Grow => FitPolicy.Content,
                _ => FitPolicy.None,
            };
            o.CoverDesignRim = a.Rim == RimChoice.Replace;
            o.PadWith = a.Background == BackgroundRole.Surround ? PadFill.Untouched : PadFill.Background;
        }
        // A shaded surround the user called a surround is evened out first, so the tuner sees
        // the design where the survey did.
        o.UniformSurround = a.Background == BackgroundRole.Surround && s.SurroundShaded;
        o.ResolvePhysical(s.Width, s.Height);
        if (a.Rim == RimChoice.None) o.AddRim = false;
        if (!a.WriteDpi) o.Dpi = null;

        var flatWords = new List<string>();
        for (int k = 0; k < s.FlatAreas.Count; k++)
        {
            var area = s.FlatAreas[k];
            var mode = InScope(area, a.FlatScope) && k < a.FlatModes.Length ? a.FlatModes[k] : FlatMode.Leave;
            o.FlatActions.Add(area.ToAction(mode, s.MaxValue));
            flatWords.Add(mode.ToString().ToLowerInvariant());
        }
        p.Options = o;

        // ---- the changes, each with its reason
        var c = p.Changes;
        var floor = s.Floor(p.BackgroundIsDesign, p.RimCovered);
        var top = s.Top(p.BackgroundIsDesign, p.RimCovered);
        c.Add(new($"Black point {p.Black:N0}", a.Floor switch
        {
            LevelChoice.Feature when floor.Found => $"Makes the floor ({floor.Share * 100:F0}% of the design"
                + (floor.Noise > 0 ? $", with {floor.Noise:N0} levels of roughness" : "") + ") one exact depth.",
            LevelChoice.Feature when floor.Source == "gap" =>
                $"Closes the empty gap from {floor.Low:N0} to {floor.High:N0}: the {floor.Share * 100:F2}% of the design below it - small, "
                + "already-deepest pockets - stays full depth, and the layers the gap would have spent cutting nothing new go to the relief.",
            LevelChoice.Extreme => "The design's lowest level: nothing below it to clip.",
            LevelChoice.Custom => "Set by hand.",
            _ => "Where the design's own deepest 0.1% begins, so a few stray pixels cannot hold the depth back.",
        }));
        c.Add(new($"White point {p.White:N0}", a.Top switch
        {
            LevelChoice.Feature when top.Found => $"Makes the flat top ({top.Share * 100:F0}% of the design) untouched surface.",
            LevelChoice.Feature when top.Source == "gap" =>
                $"Closes the empty gap from {top.Low:N0} to {top.High:N0}: the {top.Share * 100:F2}% of the design above it stays untouched, "
                + "and the gap's layers go to the relief.",
            LevelChoice.Extreme => "The design's highest level: nothing above it to clip.",
            LevelChoice.Custom => "Set by hand.",
            _ => "Where the design's own highest 0.1% begins: the high points become untouched surface.",
        }));
        c.Add(new("Stretch on", "Spreads what lies between the two points over the whole range, so the relief is as deep as the target says."));
        if (s.BackgroundShare >= 0.01)
            c.Add(new(p.BackgroundIsDesign ? "Background kept as a floor" : "Background treated as a surround",
                p.BackgroundIsDesign ? "It is part of the design: a cut-away floor engraved to its depth."
                                     : "It is not part of the coin, so it does not set the levels" +
                                       (a.Rim == RimChoice.None ? " - but with no rim it is still engraved where it lies." : ", and the rim leaves it uncut.")
                                       + (o.UniformSurround ? $" It is not one level ({s.BackgroundLow:N0} to {s.BackgroundHigh:N0}), so it is first made one: "
                                                              + "otherwise its shading or marks read as design out to the corners and the coin is shrunk to fit them." : "")));

        if (a.Rim != RimChoice.None)
        {
            c.Add(new($"Rim {a.RimMm:0.00} mm", "Pure white, so the laser never touches the blank's own rim."));
            if (a.Rim == RimChoice.Replace && s.DrawnRim is { } dr)
                c.Add(new("Drawn rim replaced", $"The artwork's own rim, about {dr.Width / (dr.Outer / (blankMm / 2)):0.00} mm wide, goes under "
                    + "the new one: the coin's rim is the blank's own untouched surface, with no trench beside it, and all of the "
                    + "depth goes to the design."));
            c.Add(new(o.Fit switch
            {
                FitPolicy.Design => "Blank centred on the design",
                FitPolicy.Content => "Canvas grown to clear the rim",
                _ => "Canvas kept as it is",
            }, o.Fit switch
            {
                FitPolicy.Design => (OffMm(s, blankMm) < 0.05 ? "The design is already centred; the blank is sized to it."
                                     : $"The design sits {OffMm(s, blankMm):0.0} mm from the canvas centre.") + " Only background is ever cropped.",
                FitPolicy.Content => "Pixels are copied into a larger square, never scaled, until the design clears the rim.",
                _ => "The rim paints over anything of the design that runs past it.",
            }));
            c.Add(new(a.RampMm <= 0 ? "Hard step into the rim" : $"Taper {a.RampMm:0.00} mm into the rim",
                a.RampMm <= 0 ? "The field meets the rim in one pixel; the beam softens it to about its own width."
                : a.Target == WizardTarget.MakeIt ? "MakeIt samples power every 0.1 mm along a line, so a taper needs at least 0.3 mm to arrive as more than a step or two."
                : "Several scan lines wide at a fine line interval, so the field rises smoothly to the rim."));
        }
        else c.Add(new("No rim", "The map is engraved right to its edges."));

        for (int k = 0; k < o.FlatActions.Count; k++)
        {
            var fa = o.FlatActions[k];
            if (fa.Mode == FlatMode.Leave) continue;
            var area = s.FlatAreas[k];
            c.Add(new(fa.Mode == FlatMode.Flatten ? $"Flat area {k + 1} flattened to {fa.Level:N0}" : $"Flat area {k + 1} smoothed",
                $"{area.ShareOfDesign * 100:F1}% of the design, " +
                (fa.Mode == FlatMode.Flatten ? "made one exact level so it engraves without speckle."
                                             : "pixel noise removed; its slope is kept.")));
        }

        c.Add(new(a.Target == WizardTarget.LightBurn ? $"Figures quoted at {p.Passes:N0} passes" : $"{p.Passes:N0} layers",
            a.Target switch
            {
                WizardTarget.MakeIt => $"{p.Passes:N0} x {MakeItZStepMm:0.00} mm = {p.Passes * MakeItZStepMm:0.00} mm, against a target of {depthMm:0.00} mm: Z follows the floor down.",
                WizardTarget.LightBurn => "Grayscale mode does not slice, so this is only a reference; set passes and Z step in LightBurn.",
                _ => $"{depthMm / p.Passes * 1000:0.0} um per pass to reach {depthMm:0.00} mm.",
            }));
        c.Add(new($"{o.OutputBitDepth}-bit output", o.OutputBitDepth == 8
            ? "MakeIt works in 8 bits; reducing here means the levels you see are the ones it gets."
            : "Nothing thrown away before the program that drives the laser."));
        if (o.Dpi is double dpi)
            c.Add(new($"{dpi:N0} dpi written", "The file imports at its true size on the blank."));
        if (a.Outline)
            c.Add(new("Alignment outline", "An SVG of the blank's edge and the engraved area, for framing with Hull or Contour."));

        // ---- the same thing from a command line
        string stem = Path.GetFileNameWithoutExtension(fileName);
        p.SuggestedName = stem + a.Suffix + ".png";
        var cmd = new List<string> { "DepthView", "--tune", Quote(fileName),
            "--blank", N(blankMm), "--depth-mm", N(depthMm) };
        if (a.Rim != RimChoice.None)
        {
            cmd.AddRange(new[] { "--rim-mm", N(a.RimMm), "--ramp-mm", N(a.RampMm) });
            if (a.Rim == RimChoice.Replace) cmd.Add("--cover-rim");
            else if (o.Fit == FitPolicy.Design) cmd.AddRange(new[] { "--fit", "design" });
            else if (o.Fit == FitPolicy.Content) cmd.AddRange(new[] { "--fit", "content" });
            if (o.Fit != FitPolicy.None && o.PadWith == PadFill.Untouched) cmd.AddRange(new[] { "--pad", "untouched" });
        }
        if (o.UniformSurround) cmd.Add("--uniform-surround");
        cmd.AddRange(new[] { "--black", p.Black.ToString(inv), "--white", p.White.ToString(inv),
            "--passes", p.Passes.ToString(inv), "--bits", o.OutputBitDepth.ToString(inv) });
        if (!a.WriteDpi) cmd.Add("--no-dpi");
        if (a.Outline) cmd.Add("--outline");
        if (o.FlatActions.Exists(f => f.Mode != FlatMode.Leave))
            cmd.AddRange(new[] { "--flat", string.Join(",", flatWords) });
        cmd.AddRange(new[] { "--out", Quote(p.SuggestedName) });
        p.Command = string.Join(" ", cmd);
        return p;
    }

    private static double OffMm(DesignSurvey s, double blankMm) => WizardAdvice.OffCentreMm(s, blankMm);
    private static double MakeItZStepMm => WizardAdvice.MakeItZStepMm;
    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
