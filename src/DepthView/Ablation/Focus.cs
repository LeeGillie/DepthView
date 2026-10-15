using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LumosUltra.Jobs;

namespace DepthView.Ablation;

/// <summary>
/// How the machine was focused before a coupon was cut, and what Z that is. Removal depends
/// on focus more than on any setting, so focusing is the first step of cutting any coupon;
/// the record is kept per laser, lens and coupon thickness, and when nothing has changed
/// since the last one the user can reuse it instead of focusing again.
///
/// Z is in the machine's own absolute coordinates (what a MakeIt relief job writes as
/// <c>G0Z</c>), read from the laser or typed from what MakeIt shows. A ramp cut at a series
/// of Z offsets around it shows which offset is sharpest and, as importantly, proves that
/// the number means what the jobs assume: a focus is only marked verified once a ramp was
/// cut and a bar chosen, and only a verified focus puts a Z move into a coupon job by
/// default.
/// </summary>
public sealed class FocusRecord
{
    public string LaserId { get; set; } = "";
    public string Lens { get; set; } = "";
    public double ThicknessMm { get; set; }
    public DateTimeOffset SetUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>How the machine was brought to focus: its own autofocus, MakeIt's material thickness, by hand...</summary>
    public string Method { get; set; } = "machine autofocus";
    /// <summary>The machine's Z at that focus, mm, when known.</summary>
    public double? MachineZMm { get; set; }
    /// <summary>What the laser answered when asked for its height, verbatim, for the record.</summary>
    public string? MachineReply { get; set; }
    /// <summary>Offset chosen from the ramp, mm, relative to <see cref="MachineZMm"/>. 0 without a ramp.</summary>
    public double OffsetMm { get; set; }
    /// <summary>A ramp was cut at this Z and its bars visibly differed: the Z number drives the head as the jobs assume.</summary>
    public bool ZVerified { get; set; }
    public int? RampBars { get; set; }
    public double? RampStepMm { get; set; }
    public string? RampJobFile { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>The Z a coupon job should put the head at, when the machine's Z is known.</summary>
    public double? FocusZMm => MachineZMm is { } z ? z + OffsetMm : null;

    public bool Matches(Study study) =>
        string.Equals(LaserId, study.Laser.Id, StringComparison.Ordinal)
        && string.Equals((Lens ?? "").Trim(), (study.Laser.Lens ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        && Math.Abs(ThicknessMm - study.Coupon.ThicknessMm) < 0.01;

    public string Summary
    {
        get
        {
            var z = MachineZMm is { } m
                ? OffsetMm != 0 ? $"machine Z {m:0.00} {OffsetMm:+0.00;-0.00} = {FocusZMm:0.00} mm" : $"machine Z {m:0.00} mm"
                : "Z not read";
            var v = ZVerified ? "verified by a ramp" : "not verified by a ramp";
            return $"{Method}, {z}, {v}";
        }
    }

    public string Age(DateTimeOffset now)
    {
        var t = now - SetUtc;
        return t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} min ago" : t.TotalDays < 1 ? $"{t.TotalHours:0.#} h ago" : $"{t.TotalDays:0.#} days ago";
    }
}

/// <summary>What the laser's own height measurement said, as far as it can be read.</summary>
public sealed record MachineHeight(double? HeightZ, double? Distance, string Raw)
{
    /// <summary>The number that looks like a Z, if any: "height-z" first, then "z", then "height".</summary>
    public double? BestZ => HeightZ;

    /// <summary>Reads the reply of <c>/camera/measure_distance</c> (reported as <c>{"distance":..,"height-z":..}</c>); tolerant of other names and of text that is not JSON.</summary>
    public static MachineHeight Parse(string raw)
    {
        double? z = null, d = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            z = Find(doc.RootElement, "height-z", "height_z", "heightZ", "z", "height");
            d = Find(doc.RootElement, "distance", "dist");
        }
        catch (JsonException) { }
        return new MachineHeight(z, d, raw);
    }

    private static double? Find(JsonElement e, params string[] names)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v) && Number(v) is { } x) return x;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && Find(p.Value, names) is { } x) return x;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                if (Find(item, names) is { } x) return x;
        return null;
    }

    private static double? Number(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d
        : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s
        : null;
}

/// <summary>
/// The focus ramp: a row of bars, each cut at a different Z offset around the machine's
/// focus, numbered 1..n in the metal. The bar whose single line is narrowest and whose
/// square is brightest is at focus; its offset is recorded. Bars run from -range to +range
/// in steps, so the middle bar is the machine's own focus.
/// </summary>
public static class FocusRamp
{
    public const double BarPitchMm = 3.0, LineMm = 6.0, SquareMm = 2.0, DigitMm = 1.8;
    public const double MaxRangeMm = 2.0, MinStepMm = 0.05;

    /// <summary>Offsets, lowest first; always an odd count with 0 in the middle.</summary>
    public static double[] Offsets(double rangeMm, double stepMm)
    {
        rangeMm = Math.Clamp(Math.Abs(rangeMm), 0, MaxRangeMm);
        stepMm = Math.Max(Math.Abs(stepMm), MinStepMm);
        int half = (int)Math.Round(rangeMm / stepMm);
        return Enumerable.Range(-half, 2 * half + 1).Select(i => Math.Round(i * stepMm, 3)).ToArray();
    }

    public static double WidthMm(int bars) => bars * BarPitchMm;
    public static double HeightMm => LineMm + 0.6 + SquareMm + 0.6 + DigitMm + 0.5;

    /// <summary>The ramp job, centred at (cx, cy) on the bed. Every bar: <c>G0Z</c> to its height, one powered line across, a small filled square. The numbers are cut last at the base Z.</summary>
    public static string Gcode(Study study, double baseZ, double rangeMm, double stepMm, double cx, double cy, double powerPercent, double speedMmPerS)
    {
        var offsets = Offsets(rangeMm, stepMm);
        var job = new MakeItJob();
        var m = study.IdMark;
        double pitch = m.LinePitchMm > 0 ? m.LinePitchMm : 0.02;
        int power = MakeItJob.PowerS(Math.Clamp(powerPercent, 0, 100));
        job.Pulse(m.FrequencyKHz, study.Laser.Kind == LaserKind.Mopa ? m.PulseWidthNs : 0).Feed(Math.Max(1, speedMmPerS));
        double x0 = cx - WidthMm(offsets.Length) / 2, top = cy - HeightMm / 2;
        for (int i = 0; i < offsets.Length; i++)
        {
            double bx = x0 + i * BarPitchMm + BarPitchMm / 2;
            job.MoveZ(Math.Round(baseZ + offsets[i], 3));
            job.LineY(bx, top, top + LineMm, power);                               // the single line, read for width
            double sy = top + LineMm + 0.6;
            job.Fill(bx - SquareMm / 2, sy, bx + SquareMm / 2, sy + SquareMm, pitch, power, 1);
        }
        job.MoveZ(Math.Round(baseZ, 3));
        double dy = top + LineMm + 0.6 + SquareMm + 0.6 + DigitMm;
        for (int i = 0; i < offsets.Length; i++)
        {
            string text = (i + 1).ToString(CultureInfo.InvariantCulture);
            double w = StrokeFont.Width(text, DigitMm);
            double bx = x0 + i * BarPitchMm + BarPitchMm / 2 - w / 2;
            foreach (var line in StrokeFont.Polylines(text, bx, dy, DigitMm))
                for (int k = 0; k + 1 < line.Count; k++)
                    job.Band(line[k].X, line[k].Y, line[k + 1].X, line[k + 1].Y, CouponJob.DigitStrokeMm, pitch, power, 1);
        }
        return job.Finish();
    }

    /// <summary>Every Z the job moves to, in order of first appearance - the check that the ramp goes only where it was told to.</summary>
    public static List<double> ZMoves(string gcode)
    {
        var list = new List<double>();
        foreach (var raw in gcode.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("G0Z", StringComparison.OrdinalIgnoreCase)) continue;
            if (double.TryParse(line[3..], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) && !list.Contains(z)) list.Add(z);
        }
        return list;
    }

    /// <summary>Refuses a job whose Z moves stray outside the band the user agreed to.</summary>
    public static string? ZAudit(string gcode, double minZ, double maxZ)
    {
        var zs = ZMoves(gcode);
        if (zs.Count == 0) return null;
        var bad = zs.Where(z => z < minZ - 1e-6 || z > maxZ + 1e-6).ToList();
        return bad.Count == 0 ? null : $"Not sent: the job moves Z to {bad[0]:0.###} mm, outside the agreed {minZ:0.##} to {maxZ:0.##} mm.";
    }

    /// <summary>The bed box the ramp occupies, mm.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Extents(int bars, double cx, double cy)
    {
        double w = WidthMm(bars), h = HeightMm;
        return (cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
    }
}
