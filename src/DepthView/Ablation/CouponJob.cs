using System;
using System.Collections.Generic;
using System.Linq;
using LumosUltra.Jobs;

namespace DepthView.Ablation;

/// <summary>
/// The job that cuts one coupon: every settings patch for its layers, the test number as a
/// QR code and digits, and (optionally) corner crosses so a camera picture of the coupon can
/// be registered later. Written with the shared <see cref="MakeItJob"/>, so the preamble,
/// postamble and stroke encoding are the ones confirmed on the machine.
///
/// All patches are cut at the focus recorded for the coupon (<see cref="FocusRecord"/>). The
/// job moves nothing in Z unless the focus height is known and verified, in which case it
/// puts the head there first (as a MakeIt relief job does) and patches with a focus offset
/// step from it. A layer's raster turns by the setting's hatch step each layer, as MakeIt's
/// Auto Planning does.
///
/// Every job goes through <see cref="Audit"/> before it is sent, and nothing above the
/// study's energy ceiling leaves the program, whatever produced it.
/// </summary>
public static class CouponJob
{
    /// <summary>Width of the engraved digits' strokes, mm.</summary>
    public const double DigitStrokeMm = 0.15;

    /// <param name="focusZMm">The focus height to put the head at (an absolute <c>G0Z</c>, the machine's own
    /// coordinates), or null to leave Z where the machine is. Required when any patch carries a focus offset.</param>
    public static string Gcode(Study study, Coupon coupon, double centreX, double centreY, bool cornerMarks = true, double? focusZMm = null)
    {
        var l = study.Layout;
        double ox = centreX - l.Coupon.BoxWidthMm / 2, oy = centreY - l.Coupon.BoxHeightMm / 2;
        var job = new MakeItJob();
        if (focusZMm is { } z0) job.MoveZ(Math.Round(z0, 3));
        double currentZ = focusZMm ?? double.NaN;

        // Settings patches, in the coupon's order (already randomised by the design).
        double lead = Math.Clamp((l.GapMm - 0.4) / 2, 0, 0.5);
        foreach (var patch in coupon.Patches)
        {
            if (patch.Index < 0 || patch.Index >= l.Capacity) continue;
            var cell = l.Cells[patch.Index];
            var s = patch.Settings;
            double pitch = s.LinePitchMm;
            if (!(pitch > 0)) continue;
            if (s.FocusOffsetMm != 0)
            {
                if (focusZMm is null) throw new InvalidOperationException($"Patch {patch.Label} asks for a focus offset of {s.FocusOffsetMm:+0.##;-0.##} mm, which needs the focus height in the job.");
                double want = Math.Round(focusZMm.Value + s.FocusOffsetMm, 3);
                if (want != currentZ) { job.MoveZ(want); currentZ = want; }
            }
            else if (focusZMm is { } back && currentZ != back) { job.MoveZ(Math.Round(back, 3)); currentZ = back; }
            job.Pulse(s.FrequencyKHz, study.Laser.Kind == LaserKind.Mopa ? s.PulseWidthNs : 0).Feed(s.SpeedMmPerS);
            int power = MakeItJob.PowerS(s.PowerPercent);
            double x0 = ox + cell.X, y0 = oy + cell.Y, x1 = x0 + l.PatchSizeMm, y1 = y0 + l.PatchSizeMm;
            for (int layer = 0; layer < Math.Max(1, s.Layers); layer++)
            {
                double angle = ((s.HatchStepDeg * layer) % 180 + 180) % 180;
                job.Hatch(x0, y0, x1, y1, pitch, angle, power, lead);
            }
        }
        if (focusZMm is { } zEnd && currentZ != zEnd) job.MoveZ(Math.Round(zEnd, 3));

        // The test number: QR in the ID square, digits in the margin below the grid, crosses.
        if (l.IdCell is { } id)
        {
            var m = study.IdMark;
            double mp = m.LinePitchMm > 0 ? m.LinePitchMm : 0.02;
            job.Pulse(m.FrequencyKHz, study.Laser.Kind == LaserKind.Mopa ? m.PulseWidthNs : 0).Feed(m.SpeedMmPerS);
            int power = MakeItJob.PowerS(m.PowerPercent);
            foreach (var (x, y, size) in TestNumberMark.Squares(coupon.Number, ox + id.X, oy + id.Y, l.PatchSizeMm))
                job.Fill(x, y, x + size, y + size, mp, power, Math.Max(1, m.Layers));

            foreach (var line in DigitStrokes(study, coupon.Number, ox, oy))
                for (int i = 0; i + 1 < line.Count; i++)
                    job.Band(line[i].X, line[i].Y, line[i + 1].X, line[i + 1].Y, DigitStrokeMm, mp, power, Math.Max(1, m.Layers));

            if (cornerMarks)
                foreach (var (cx, cy) in CrossCentres(study, ox, oy))
                {
                    double half = CrossMm / 2, bar = 0.25;
                    job.Fill(cx - half, cy - bar / 2, cx + half, cy + bar / 2, mp, power, Math.Max(1, m.Layers));
                    job.Fill(cx - bar / 2, cy - half, cx + bar / 2, cy + half, mp, power, Math.Max(1, m.Layers));
                }
        }
        return job.Finish();
    }

    /// <summary>Arm length of the corner crosses, mm.</summary>
    public const double CrossMm = 1.5;

    /// <summary>True when some patch carries a focus offset, so the job cannot be built without the focus height.</summary>
    public static bool NeedsFocusZ(Coupon coupon) => coupon.Patches.Any(p => p.Settings.FocusOffsetMm != 0);

    /// <summary>The digits "DV 123" under the grid, in machine mm, or nothing when the margin has no room for them.</summary>
    public static List<List<(double X, double Y)>> DigitStrokes(Study study, int number, double ox, double oy)
    {
        var l = study.Layout;
        if (l.Cells.Count == 0) return [];
        string text = $"{TestNumberMark.Prefix} {number}";
        if (!StrokeFont.CanDraw(text)) text = number.ToString();
        double bottom = l.Cells.Max(c => c.Y) + l.PatchSizeMm;               // grid's lower edge, coupon mm
        double room = l.Coupon.BoxHeightMm - bottom - 0.3;                     // to the coupon's edge
        if (l.Coupon.Shape == CouponShape.Coin) room = Math.Min(room, l.MarginMm + l.GapMm);
        double height = Math.Min(l.LabelHeightMm, room - 0.4);
        if (height < 1.0) return [];
        if (l.Coupon.Shape == CouponShape.Coin) return CoinDigits(l, text, bottom, height, ox, oy);
        // Start clear of the bottom-left cross, which sits in the same margin.
        double left = l.Cells.Min(c => c.X) + (l.MarginMm >= CrossMm + 0.4 ? CrossMm + 0.8 : 0);
        double width = StrokeFont.Width(text, height);
        double right = l.Cells.Max(c => c.X) + l.PatchSizeMm;
        if (width > right - left) height *= (right - left) / width;
        if (height < 1.0) return [];
        return StrokeFont.Polylines(text, ox + left, oy + bottom + 0.3 + height, height);
    }

    /// <summary>
    /// On a round blank the box under the grid is mostly off the metal (the first coin
    /// layout drew "DV 1" past the edge). Keep the digits inside the same circle the patches
    /// keep inside (diameter less the margin, which also keeps them off a raised rim), clear of
    /// the bottom-left cross, centred in the room left; shrink them to fit, or leave them
    /// off (the QR code still carries the number) when under 1 mm.
    /// </summary>
    private static List<List<(double X, double Y)>> CoinDigits(PatchLayout l, string text, double bottom, double height, double ox, double oy)
    {
        double r = l.Coupon.DiameterMm / 2 - l.MarginMm;
        double cx = l.Coupon.BoxWidthMm / 2, cy = l.Coupon.BoxHeightMm / 2;
        double top = bottom + 0.3;
        bool crosses = l.MarginMm >= CrossMm + 0.4;
        double crossClear = cx - l.PatchSizeMm + CrossMm / 2 + 0.4;          // right end of the bottom-left cross, plus a gap
        for (int k = 0; k < 10 && height >= 1.0; k++)
        {
            double below = top + height - cy;                                  // the text's lower edge, below the centre
            if (below >= r) return [];
            double half = Math.Sqrt(r * r - below * below);
            double lo = cx - half, hi = cx + half;
            if (crosses) lo = Math.Max(lo, crossClear);
            double width = StrokeFont.Width(text, height);
            if (width <= hi - lo)
                return StrokeFont.Polylines(text, ox + lo + (hi - lo - width) / 2, oy + top + height, height);
            height *= Math.Max(0.5, (hi - lo) / width) * 0.98;
        }
        return [];
    }

    /// <summary>Three crosses in the margin, at the top-left, top-right and bottom-left of the grid (never bottom-right, so a picture's way up is unambiguous).</summary>
    public static IEnumerable<(double X, double Y)> CrossCentres(Study study, double ox, double oy)
    {
        var l = study.Layout;
        if (l.Cells.Count == 0 || l.MarginMm < CrossMm + 0.4) yield break;
        double left = l.Cells.Min(c => c.X), right = l.Cells.Max(c => c.X) + l.PatchSizeMm;
        double top = l.Cells.Min(c => c.Y), bottom = l.Cells.Max(c => c.Y) + l.PatchSizeMm;
        double off = CrossMm / 2 + 0.3;
        if (l.Coupon.Shape == CouponShape.Coin)
        {
            // On a round blank the corners of the grid's box are outside the metal: put the
            // crosses on the vertical centre line instead, above and below the grid.
            double cx = l.Coupon.BoxWidthMm / 2;
            yield return (ox + cx - l.PatchSizeMm, oy + top - off);
            yield return (ox + cx + l.PatchSizeMm, oy + top - off);
            yield return (ox + cx - l.PatchSizeMm, oy + bottom + off);
            yield break;
        }
        yield return (ox + left - off, oy + top - off);
        yield return (ox + right + off, oy + top - off);
        yield return (ox + left - off, oy + bottom + off);
    }

    /// <summary>The bed box the coupon occupies, mm: what the camera preview outlines and what must stay on the bed.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Extents(Study study, double centreX, double centreY)
    {
        double w = study.Coupon.BoxWidthMm, h = study.Coupon.BoxHeightMm;
        return (centreX - w / 2, centreY - h / 2, centreX + w / 2, centreY + h / 2);
    }

    /// <summary>
    /// The independent check of the finished job against the study's energy ceiling: energy
    /// per square millimetre read from the G-code itself, so every layer counts whatever the
    /// design said. Fails closed on anything it cannot read.
    /// </summary>
    public static JobAudit.Result Audit(Study study, string gcode)
    {
        // The audit counts lines into 1 mm squares and can read up to one line's worth high
        // (JobAudit's own note; Atlas keeps its audit limit above its planner's for this). A
        // raster at an angle crosses a square along at most its diagonal, so one extra line is
        // at most sqrt(2) x pitch of the square's 1/pitch lines. Without this allowance a patch
        // planned exactly at the ceiling is refused on counting error alone (DV1, 2026-10-15).
        double limit = study.EnergyCeilingJPerMm2 * AuditAllowance(study);
        var r = JobAudit.Check(gcode, study.Laser.RatedPowerW, limit);
        if (!double.IsFinite(r.MaxJPerMm2)) return r;
        string msg = r.Ok
            ? $"Heat check passed: at most {r.MaxJPerMm2:0.0} J/mm² anywhere (ceiling {study.EnergyCeilingJPerMm2:0.#}, audit limit {limit:0.0} with its line-counting allowance)."
            : $"Not sent: the job would put {r.MaxJPerMm2:0.0} J/mm² into the metal around X {r.AtXMm:0} Y {r.AtYMm:0} mm, over the audit limit of {limit:0.0} J/mm² " +
              $"(ceiling {study.EnergyCeilingJPerMm2:0.#} plus its line-counting allowance). That much heat cuts into or through the blank.";
        return r with { Message = msg };
    }

    /// <summary>1 + sqrt(2) x the widest line pitch the coupon jobs use, mm per 1 mm square; never more than +10 %.</summary>
    public static double AuditAllowance(Study study)
    {
        double pitch = study.Design.Select(s => s.LinePitchMm).Append(study.IdMark.LinePitchMm)
            .Where(p => double.IsFinite(p) && p > 0).DefaultIfEmpty(0.02).Max();
        return 1 + Math.Min(0.10, Math.Sqrt(2) * pitch);
    }

    /// <summary>Patches whose planned total dose is over the ceiling, hottest first - caught before a job is even built.</summary>
    public static IReadOnlyList<(string Label, double JPerMm2)> TooHot(Study study, IEnumerable<Patch> patches) =>
        patches.Select(p => (p.Label, J: p.Settings.TotalDoseJPerMm2(study.Laser.RatedPowerW)))
               .Where(x => !double.IsFinite(x.J) || x.J > study.EnergyCeilingJPerMm2 + 1e-9)
               .OrderByDescending(x => x.J).ToList();

    /// <summary>A rough cutting time: burn length over speed per layer, plus travel at MakeIt's travel feed.</summary>
    public static TimeSpan Estimate(Study study, Coupon coupon)
    {
        var l = study.Layout;
        double seconds = 0;
        foreach (var p in coupon.Patches)
        {
            var s = p.Settings;
            if (!(s.LinePitchMm > 0) || s.SpeedMmPerS <= 0) continue;
            double lines = l.PatchSizeMm / s.LinePitchMm;
            double perLayer = lines * (l.PatchSizeMm / s.SpeedMmPerS + l.PatchSizeMm / 250.0);
            seconds += perLayer * Math.Max(1, s.Layers);
        }
        seconds += 20; // the number, crosses, homing
        return TimeSpan.FromSeconds(seconds);
    }
}
