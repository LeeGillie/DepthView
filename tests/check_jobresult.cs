#:project ../src/DepthView/DepthView.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
// Job results as evidence: `dotnet run tests/check_jobresult.cs` (a .NET 10 file-based app
// against the DepthView project). On a scratch database with no reference studies, brass on
// the 100 W MOPA is uncalibrated; recording the depth a real job reached makes it grade-A
// evidence the advice uses, a second job at another speed gives it something to interpolate,
// deleting a coupon record removes that evidence and nothing else, and every step survives a
// save and reload. Exit 1 on the first failure.
using System;
using System.IO;
using System.Linq;
using DepthView.Ablation;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures++; }

var path = Path.Combine(Path.GetTempPath(), "dv-jobresult-" + Guid.NewGuid().ToString("N")[..6] + ".json");
try
{
    var db = AblationDatabase.Load(path);
    var brass = MaterialSpec.Presets.First(m => m.Id == "brass-h62");
    var laser = LaserSpec.Presets[0];

    var before = SettingsAdvisor.Advise(db, brass, laser, 1.1, 256, laser.Lens);
    Check(before.Recipe is null && before.Grade == "E", "empty database: brass uncalibrated, no recipe, grade E");
    Check(before.ReferenceStudiesMissing, "empty database: the advice says the reference studies are missing");

    var settings = new PatchSettings { PowerPercent = 100, SpeedMmPerS = 2000, LineDensityPerCm = 300, FrequencyKHz = 100, PulseWidthNs = 350, Layers = 256, HatchStepDeg = 27 };
    var m = new PatchMeasurement { ReadingsUm = [1080, 1095, 1070], Method = "gauge", Notes = "check", MeasuredUtc = DateTimeOffset.UtcNow };
    var (study, coupon) = db.RecordJobResult(brass, laser, settings, m, "coin front", "");
    db.Save();
    Check(coupon.Number == 1 && db.NextTestNumber == 2, "first job result is DV1 and spends a test number");
    Check(study.Name == "Jobs - Brass H62 on " + laser.Name && !study.Reference && study.Origin == "local", "it lives in a local 'Jobs - material on laser' study");
    Check(coupon.Patches.Count == 1 && coupon.Patches[0].Status == PatchStatus.Measured && coupon.Patches[0].Label == "job", "one measured patch labelled 'job'");

    var second = db.RecordJobResult(brass, laser, settings with { SpeedMmPerS = 1500 }, m with { ReadingsUm = [1400] }, "coin back", "");
    db.Save();
    Check(ReferenceEquals(second.Study, study) && study.Coupons.Count == 2, "a second job on the same material and laser joins the same study");

    var reload = AblationDatabase.Load(path);
    var ev = Evidence.Gather(reload);
    Check(ev.Count == 2 && ev.All(e => e.Grade == "A" && e.MeasuredHere), "after reload: two grade-A observations, measured here");
    Check(Math.Abs(ev[0].DepthUm - 1081.67) < 0.1 && ev[0].Layers == 256, "the mean of the readings over the job's layers");

    var after = SettingsAdvisor.Advise(reload, brass, laser, 1.1, 256, laser.Lens);
    Check(after.Recipe is not null && after.Grade == "A", "advice now has a grade-A recipe");
    Check(after.Recipe is { } r && r.SpeedMmPerS > 1500 && r.SpeedMmPerS < 2000 && Math.Abs(r.PulseWidthNs - 350) < 1e-9, "speed interpolated between the two jobs, pulse width kept");
    Check(after.PredictedTotalMm is { } t && Math.Abs(t - 1.1) < 0.05, "predicted depth lands on the target");
    Check(after.Used.Count == 2 && after.Used.All(u => u.Source.StartsWith("measured here")), "both job results are the evidence used");

    var s2 = reload.Studies.First(s => s.Name == study.Name);
    Check(AblationDatabase.DeleteCoupon(s2, s2.Coupons.Last()), "delete the second coupon record");
    reload.Save();
    var reload2 = AblationDatabase.Load(path);
    var s3 = reload2.Studies.First(s => s.Name == study.Name);
    Check(s3.Coupons.Count == 1 && reload2.NextTestNumber == 3, "one record left, the test number not reused");
    Check(Evidence.Gather(reload2).Count == 1, "one observation left");
    var one = SettingsAdvisor.Advise(reload2, brass, laser, 1.1, 256, laser.Lens);
    Check(one.Recipe is not null && one.Grade == "A" && one.Used.Count == 1, "advice from the one remaining job result");
    Check(DepthView.Views.AblationWindow.StudyStatus(s3) == "1 job result recorded - 1 measured patch - grade A evidence", "status line: " + DepthView.Views.AblationWindow.StudyStatus(s3));

    // Clearing a patch's readings by the measure dialog's rule: measurement null, status back to Engraved.
    var p = s3.Coupons[0].Patches[0] with { Measurement = null, Status = PatchStatus.Engraved };
    s3.Coupons[0].Patches = [p];
    Check(Evidence.Gather(reload2).Count == 0, "a cleared patch is no longer evidence");
}
finally { try { File.Delete(path); } catch { } }

Console.WriteLine(failures == 0 ? "Job results: all checks passed." : $"Job results: {failures} check(s) failed.");
return failures == 0 ? 0 : 1;
