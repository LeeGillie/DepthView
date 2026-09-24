using System;

namespace DepthView.Rendering;

/// <summary>
/// The one definition of how deep a relief preview is drawn, shared by the tuning dialog, the
/// standalone relief window and the headless --render.
///
/// Depth is stated physically: a target depth in millimetres (the deepest cut) on a blank of a
/// given diameter, plus an exaggeration in stops - doublings - around that depth. 0 stops is
/// true scale, Z drawn in the same millimetres as X and Y, which is the only setting that shows
/// what the piece will actually look like. Anything else is a magnifier for finding terracing,
/// noise and banding, and says so in colour: green at true scale, through yellow, to red.
///
/// This replaced a raw renderer ratio in which "1.0" drew the relief an eighth of the field
/// width deep - 5 mm on a 40 mm blank, three to five times any real laser relief - while
/// reading like "no exaggeration".
/// </summary>
public static class ZScale
{
    public const double MinStops = -8;
    public const double MaxStops = 6;

    /// <summary>Within this many stops of zero counts as true scale, so a slider can land on it.</summary>
    public const double Detent = 0.1;

    public const double DefaultTargetMm = 0.40;
    public const double DefaultBlankMm = 40.0;

    /// <summary>The bottom of the travel draws no depth at all, rather than one more halving.</summary>
    public static bool IsFlat(double stops) => stops <= MinStops + 1e-9;

    public static bool IsTrueScale(double stops) => !IsFlat(stops) && Math.Abs(stops) < 1e-9;

    /// <summary>Drawn depth over real depth: 1 at true scale, 0 when flat.</summary>
    public static double Ratio(double stops) => IsFlat(stops) ? 0 : Math.Pow(2, stops);

    public static double DrawnDepthMm(double targetMm, double stops)
        => Math.Max(0, targetMm) * Ratio(stops);

    /// <summary>
    /// Millimetres of drawn depth into the renderer's own exaggeration figure.
    ///
    /// The renderer draws the full height range as field-width / 8 times that figure, a ratio
    /// to the picture with no physical meaning. The blank spans the short side of the field, so
    /// one field pixel is blank / min(w, h) millimetres, and a depth in millimetres becomes a
    /// height in field pixels that X and Y are measured in too.
    /// </summary>
    public static double RendererExaggeration(double drawnMm, double blankMm, int fieldW, int fieldH)
    {
        if (drawnMm <= 0 || blankMm <= 0 || fieldW <= 0 || fieldH <= 0) return 0;

        double mmPerFieldPixel = blankMm / Math.Min(fieldW, fieldH);
        double wantedPixels = drawnMm / mmPerFieldPixel;
        return wantedPixels / (fieldW / 8.0);
    }

    /// <summary>"2.8" or "16" - one decimal only where it still means something.</summary>
    private static string Num(double r) => r < 10 ? r.ToString("0.#") : r.ToString("0");

    /// <summary>Short factor for a label: "true scale", "2.8x", "1/4", "flat".</summary>
    public static string FactorText(double stops)
    {
        if (IsFlat(stops)) return "flat";
        if (IsTrueScale(stops)) return "true scale";
        return stops > 0 ? $"{Num(Math.Pow(2, stops))}x" : $"1/{Num(Math.Pow(2, -stops))}";
    }

    /// <summary>How far from true scale, 0 (true) to 1 (extreme or flat). Drives the colour.</summary>
    public static double Severity(double stops)
    {
        if (IsFlat(stops)) return 1;
        return Math.Clamp(Math.Abs(stops) / 3.0, 0, 1);
    }

    /// <summary>
    /// Text for a badge on the picture itself. It sits on the surface rather than only beside a
    /// slider because a render gets looked at, screenshotted and passed around long after
    /// anyone has checked where the slider was.
    /// </summary>
    public static string BadgeText(double stops)
    {
        if (IsFlat(stops)) return "Z flat - no depth drawn";
        if (IsTrueScale(stops)) return "Z 1:1 - true scale";

        double a = Math.Abs(stops);
        string f = stops > 0 ? $"Z x{Num(Math.Pow(2, stops))}" : $"Z x1/{Num(Math.Pow(2, -stops))}";
        string what = a >= 3 ? "inspection only" : "not to scale";
        return $"{f} - {what}";
    }

    /// <summary>A sentence saying what the current setting means for what is on screen.</summary>
    public static string Verdict(double stops)
    {
        if (IsFlat(stops))
            return "Flat: no depth is drawn at all. Only the material and texture are left.";
        if (IsTrueScale(stops))
            return "True scale: Z is drawn in the same millimetres as X and Y. This is how the relief will actually look.";

        double r = Math.Pow(2, Math.Abs(stops));
        if (stops > 0)
        {
            string lead = Math.Abs(stops) >= 3 ? "Inspection only. " : "Not to scale. ";
            return lead + $"Depth is drawn {Num(r)} times deeper than it will cut, to magnify terracing, " +
                   "noise and banding. The real piece will look flatter and smoother than this.";
        }

        return $"Not to scale. Depth is drawn at 1/{Num(r)} of what will be cut, " +
               "so the real piece will look deeper than this.";
    }

    /// <summary>
    /// Continuous hint colour: green at true scale, yellow at 1 stop (2x), red from 3 stops (8x,
    /// where the badge also switches to "inspection only") and when flat. Squashing is treated
    /// like stretching - neither is to scale. Kept bright enough to read on the dark panels.
    /// </summary>
    public static (byte R, byte G, byte B) HintRgb(double stops)
    {
        double hue;
        if (IsFlat(stops)) hue = 2;
        else
        {
            double a = Math.Abs(stops);
            hue = a <= 1.0
                ? 125 + (52 - 125) * a
                : 52 + (2 - 52) * Math.Min(1, (a - 1.0) / 2.0);
        }
        return Hsv(hue, 0.70, 0.93);
    }

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        static byte B(double t) => (byte)Math.Round(Math.Clamp(t, 0, 1) * 255);
        return (B(r + m), B(g + m), B(b + m));
    }
}
