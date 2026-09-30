using System;
using System.Linq;
using System.Text;

namespace DepthView.Integrations.WeCreat.Gcode;

/// <summary>Plain-text rendering of a <see cref="GcodeAnalysis"/>, for --gcode.</summary>
public static class GcodeReport
{
    /// <summary>
    /// Said on every report, because the numbers below are only as good as the decoding and
    /// the decoding has only been confirmed for one kind of MakeIt job.
    /// </summary>
    public const string EncodingNote =
        "Power as S/10 percent, frequency from M38F, pulse width from M39P and speed from G1 F " +
        "were confirmed for MakeIt's Color Test (Fine Color Marking) by a controlled run and a " +
        "physical part. They are assumed, not yet confirmed, for relief and other job types. " +
        "Scan-line spacing and the spacing of power changes along a line are measured from the " +
        "moves themselves.";

    public static string Build(GcodeAnalysis a)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DepthView G-code report");
        sb.AppendLine(new string('=', 74));
        sb.AppendLine();

        sb.AppendLine("FILE");
        sb.AppendLine($"  Name              {System.IO.Path.GetFileName(a.Path)}");
        sb.AppendLine($"  Size              {a.Bytes:N0} bytes{(a.Gzipped ? " (gzip)" : "")}, {a.Lines:N0} lines");
        if (a.Generator is { } gen) sb.AppendLine($"  Written by        {gen}");
        foreach (var c in a.HeaderComments.Skip(a.Generator is null ? 0 : 1).Take(4))
            sb.AppendLine($"  Header            {c}");
        sb.AppendLine();

        if (!a.HasBurn)
        {
            sb.AppendLine("No burning moves (G1 with S > 0) were found. Nothing in this file fires the laser.");
            AppendFooter(sb, a);
            return sb.ToString();
        }

        sb.AppendLine("WHAT THE MACHINE IS SENT");
        sb.AppendLine($"  Power levels      {a.PowerLevels.Count:N0} distinct, S {a.PowerLevels.Keys.First() / 1000.0:0.###}"
                    + $" .. {a.PowerLevels.Keys.Last() / 1000.0:0.###}  ({S(a.PowerLevels.Keys.First())} .. {S(a.PowerLevels.Keys.Last())})");
        if (a.StepMode is { } xs)
            sb.AppendLine($"  Along each line   power changes most often every {xs.Mm:0.###} mm"
                        + $"  ({xs.Share * 100:0}% of {a.StepCount:N0} changes, to the nearest {GcodeAnalysis.StepBinUm} um)");
        else
            sb.AppendLine("  Along each line   power never changes within a line");
        var dirs = a.Directions.Where(d => d.BurnLengthMm >= 0.02 * a.BurnLengthMm).Take(6).ToList();
        foreach (var d in dirs)
            sb.AppendLine($"  Scan at {d.AngleDeg,3} deg    "
                        + (d.PitchMm is double p
                            ? $"{d.Passes:N0} pass{(d.Passes == 1 ? "" : "es")}, typically {d.LinesPerPass:N0} lines"
                              + $" {p:0.#####} mm apart ({10 / p:0.#} per cm)"
                            : "not a raster (strokes or fragments)")
                        + $"  - {d.BurnLengthMm / a.BurnLengthMm * 100:0}% of the burning");
        if (a.Directions.Count > dirs.Count)
            sb.AppendLine($"                    and {a.Directions.Count - dirs.Count:N0} other direction(s) with less burning each");
        sb.AppendLine($"  Burn area         X {a.MinX:0.###} .. {a.MaxX:0.###}, Y {a.MinY:0.###} .. {a.MaxY:0.###} mm"
                    + $"  ({a.MaxX - a.MinX:0.###} x {a.MaxY - a.MinY:0.###} mm)");
        sb.AppendLine($"  Moves             {a.BurnMoves:N0} burning, {a.G1Moves:N0} G1 and {a.G0Moves:N0} G0 in all");
        sb.AppendLine($"  Distance          {a.BurnLengthMm / 1000:0.###} m burning, {a.TravelLengthMm / 1000:0.###} m with the laser off");
        sb.AppendLine();

        sb.AppendLine("  The power-level count is what limits depth resolution through this file: however");
        sb.AppendLine("  many grey levels the source map had, the machine receives this many. The spacing");
        sb.AppendLine("  figures are the sampling the machine actually gets, whatever the source resolution.");
        sb.AppendLine();

        sb.AppendLine("CUTTING HEIGHTS");
        if (a.ZLevels.Count == 0)
            sb.AppendLine("  No Z moves carried a burn. The whole job runs at one height.");
        else
        {
            sb.AppendLine($"  {a.ZLevels.Count:N0} height(s) with burning{(a.ZLevelsTruncated ? " (list truncated)" : "")}:");
            var first = a.ZLevels[0].Z;
            if (a.ZLevels.Count > 1)
                sb.AppendLine($"  from Z {first:0.###} to {a.ZLevels[^1].Z:0.###}"
                            + $" - {Math.Abs(a.ZLevels[^1].Z - first) / (a.ZLevels.Count - 1) * 1000:0.#} um per height on average");
            foreach (var z in a.ZLevels.Take(12))
                sb.AppendLine($"    Z {z.Z,9:0.###}   {z.BurnMoves,12:N0} moves   {z.BurnLengthMm / 1000,10:0.###} m"
                            + (z.MainAngleDeg is int ang ? $"   mostly at {ang} deg" : ""));
            if (a.ZLevels.Count > 12) sb.AppendLine($"    ... {a.ZLevels.Count - 12:N0} more in --json");
        }
        if (a.UndefinedOperands > 0)
            sb.AppendLine($"  {a.UndefinedOperands:N0} line(s) carry a non-numeric operand (\"undefined\"); those values were ignored, not read as zero.");
        sb.AppendLine();

        sb.AppendLine("SETTINGS, IN MAKEIT'S UNITS");
        sb.AppendLine("  One group per combination of frequency, pulse width and speed, in order of first use.");
        foreach (var g in a.Groups)
        {
            sb.AppendLine($"  [{g.Index}]  power {S(Key(g.MinS))}{(g.MaxS > g.MinS ? " .. " + S(Key(g.MaxS)) : "")}"
                        + $" ({g.PowerLevels.Count:N0} level{(g.PowerLevels.Count == 1 ? "" : "s")})"
                        + $"   speed {Opt(g.SpeedMmPerS, "0.#", " mm/s")}"
                        + $"   frequency {Opt(g.FrequencyKHz, "0.##", " kHz")}"
                        + $"   pulse width {Opt(g.PulseWidthNs, "0.##", " ns")}");
            sb.AppendLine($"       line density {(g.LineDensityPerCm is double ld ? $"{ld:0.#} /cm per pass, scanning at {string.Join("/", g.RasterAngles)} deg" : "not a raster")}"
                        + $"   {g.BurnMoves:N0} moves, {g.BurnLengthMm / 1000:0.###} m burning, first at line {g.FirstLine:N0}");
        }
        sb.AppendLine();

        if (a.Groups.Count > 1)
        {
            sb.AppendLine("ORDER OF WORK");
            sb.AppendLine($"  The job switches settings {a.TotalRuns - 1:N0} time(s). In order:");
            var runs = a.Runs.Take(40).ToList();
            sb.AppendLine("    " + string.Join(" ", runs.Select(r => $"[{r.Group}]")) + (a.TotalRuns > runs.Count ? " ..." : ""));
            sb.AppendLine("  A second group that recurs at a regular interval is how a cleaning pass shows up.");
            sb.AppendLine();
        }

        AppendFooter(sb, a);
        return sb.ToString();
    }

    private static void AppendFooter(StringBuilder sb, GcodeAnalysis a)
    {
        sb.AppendLine("HOW THIS WAS DECODED");
        sb.AppendLine(DepthView.Analysis.Fmt.Wrap(EncodingNote, 72));
        sb.AppendLine();
        if (a.MCodes.Count > 0)
            sb.AppendLine("  M codes seen      " + string.Join(", ", a.MCodes.Select(kv => $"M{kv.Key} x{kv.Value:N0}")));
        sb.AppendLine($"Read in {a.Elapsed.TotalSeconds:0.00} s.");
    }

    /// <summary>S as MakeIt's power percentage, which is S/10 in every confirmed case.</summary>
    private static string S(long key) => $"{key / 10000.0:0.##}%";
    private static long Key(double s) => (long)Math.Round(s * 1000);
    private static string Opt(double? v, string fmt, string unit) => v is double d ? d.ToString(fmt) + unit : "not set";
}
