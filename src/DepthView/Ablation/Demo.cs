using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DepthView.Ablation;

/// <summary>
/// A worked example of the study for checks and screenshots: brass H62 sheet, 4 x 4 in by
/// 3 mm, on the 100 W MOPA, a small design, one coupon planned. Lives in a temporary
/// database so it never touches the person's own. "SIMULATED" is in its name so it cannot be
/// mistaken for a real study.
/// </summary>
public static class Demo
{
    public static (AblationDatabase Db, Study Study, Coupon Coupon) Build()
    {
        var path = Path.Combine(Path.GetTempPath(), $"depthview-ablation-demo-{Environment.ProcessId}.json");
        var db = AblationDatabase.Load(path);
        db.Studies.Clear();
        db.NextTestNumber = 1;
        var laser = LaserSpec.Presets.First(l => l.Id == "lumos-ultra-mopa-100w");
        var study = new Study
        {
            Name = "Brass H62 sheet, MOPA 100 W (SIMULATED demo)",
            Material = MaterialSpec.Presets.First(m => m.Id == "brass-h62"),
            Laser = laser,
            Coupon = CouponSpec.Sheet4Inch,
            PatchSizeMm = 5, GapMm = 1.5, MarginMm = 3,
            Notes = "Demo study: the design is an example, nothing here was cut.",
        };
        var (design, reasons) = StudyDesign.Build(new DesignOptions
        {
            PowerPercents = [100, 70, 50, 35], SpeedsMmPerS = [300, 600, 1200, 2400], PulseWidthsNs = [250],
            FrequencyKHz = 45, LineDensityPerCm = 300, Layers = 10, Replicates = 2, Randomize = true,
            Verification = [new PatchSettings { PowerPercent = 100, SpeedMmPerS = 500, LineDensityPerCm = 300, FrequencyKHz = 45, PulseWidthNs = 250, Layers = 10 }],
        }, laser, out _);
        study.Design = design;
        study.DesignReasons = reasons;
        db.Studies.Add(study);
        var coupon = db.PlanCoupon(study)!;
        return (db, study, coupon);
    }

    /// <summary>The demo coupon's job, audited, as JSON for the checks (and the job itself when asked).</summary>
    public static string Report(out string gcode)
    {
        var (db, study, coupon) = Build();
        gcode = CouponJob.Gcode(study, coupon, 105, 105);
        var audit = CouponJob.Audit(study, gcode);
        var layout = study.Layout;

        // The focus ramp and a coupon job carrying a (made-up) focus height, checked the same way.
        const double demoZ = 40.96;
        string ramp = FocusRamp.Gcode(study, demoZ, 1.5, 0.25, 105, 105, 50, 1000);
        var rampAudit = CouponJob.Audit(study, ramp);
        var rampZ = FocusRamp.ZMoves(ramp);
        string withZ = CouponJob.Gcode(study, coupon, 105, 105, focusZMm: demoZ);
        var withZMoves = FocusRamp.ZMoves(withZ);
        var height = MachineHeight.Parse("{\"distance\":123.4,\"height-z\":40.96}");
        var o = new
        {
            schema = "depthview.ablation-demo/1",
            study = study.Name,
            material = study.Material.Summary,
            laser = new { study.Laser.Name, study.Laser.RatedPowerW, kind = study.Laser.Kind.ToString() },
            coupon = study.Coupon.Summary,
            layout = new { layout.Rows, layout.Columns, layout.Capacity, squares = layout.Cells.Count },
            design = study.Design.Count,
            couponsNeeded = study.CouponsFor(study.Design.Count),
            planned = new { number = coupon.Number, patches = coupon.Patches.Count, idText = TestNumberMark.Text(coupon.Number) },
            ceilingJPerMm2 = Math.Round(study.EnergyCeilingJPerMm2, 1),
            hottestPatchJPerMm2 = Math.Round(coupon.Patches.Max(p => p.Settings.TotalDoseJPerMm2(study.Laser.RatedPowerW)), 2),
            audit = new { audit.Ok, maxJPerMm2 = Math.Round(audit.MaxJPerMm2, 2), audit.TotalJoules, audit.Message },
            gcode = new { lines = gcode.Count(c => c == '\n'), bytes = Encoding.UTF8.GetByteCount(gcode) },
            minutes = Math.Round(CouponJob.Estimate(study, coupon).TotalMinutes, 1),
            focus = new
            {
                demoZ,
                ramp = new { bars = FocusRamp.Offsets(1.5, 0.25).Length, zMoves = rampZ, zMin = rampZ.Min(), zMax = rampZ.Max(), audit = new { rampAudit.Ok, maxJPerMm2 = Math.Round(rampAudit.MaxJPerMm2, 2) }, lines = ramp.Count(c => c == '\n') },
                couponWithZ = new { zMoves = withZMoves, lines = withZ.Count(c => c == '\n') },
                heightParse = new { height.HeightZ, height.Distance },
            },
            database = db.Path,
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }
}
