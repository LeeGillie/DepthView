using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepthView.Ablation;

/// <summary>
/// Everything the ablation study has planned, cut and measured, in one JSON file under the
/// person's application data (<c>%APPDATA%\DepthView\ablation\ablation.json</c>; the per-user
/// data folder on macOS and Linux), with every job sent kept beside it. Test numbers come
/// from here and only go up: a removed coupon is retired, never renumbered, so a number
/// engraved on a piece of metal always means one thing.
///
/// Studies travel: <see cref="Export"/> writes one study (settings, coupons, measurements)
/// as a file another person can <see cref="Import"/>, where it keeps its numbers and is
/// marked with where it came from. Measured results are theirs for their laser; what is
/// comparable across lasers is the physics (dose, pulse energy, overlap), and the 60/100 W
/// conversion carries settings by watts as predictions (<see cref="PowerConversion"/>).
/// </summary>
public sealed class AblationDatabase
{
    public const string Schema = "depthview.ablation/1";

    [JsonPropertyName("$schema")] public string SchemaName { get; set; } = Schema;
    public int NextTestNumber { get; set; } = 1;
    public List<Study> Studies { get; set; } = [];
    /// <summary>Every focus recorded on this machine, newest last; the latest one matching a study's laser, lens and thickness is offered for reuse.</summary>
    public List<FocusRecord> FocusHistory { get; set; } = [];
    /// <summary>The bundled seed set last offered (added or declined); 0 = never asked. See <see cref="Seeds.Version"/>.</summary>
    public int SeedsOfferedVersion { get; set; }
    public DateTimeOffset? SavedUtc { get; set; }

    /// <summary>True when the bundled seeds should be offered: a newer set than the one last offered, and none of it is here yet.</summary>
    [JsonIgnore] public bool ShouldOfferSeeds => SeedsOfferedVersion < Seeds.Version && !Seeds.All().Any(s => Studies.Any(x => x.Id == s.Study.Id));

    /// <summary>Studies cut (or to be cut) here, as opposed to reference studies and imports.</summary>
    [JsonIgnore] public IEnumerable<Study> LocalStudies => Studies.Where(s => !s.Reference && s.Origin == "local");

    /// <summary>The last focus recorded for this study's laser, lens and coupon thickness, if any.</summary>
    public FocusRecord? LastFocusFor(Study study) => FocusHistory.LastOrDefault(f => f.Matches(study));

    /// <summary>Keeps a new focus; the history is trimmed to the last fifty.</summary>
    public void RecordFocus(FocusRecord focus)
    {
        FocusHistory.Add(focus);
        if (FocusHistory.Count > 50) FocusHistory.RemoveRange(0, FocusHistory.Count - 50);
    }

    [JsonIgnore] public string Path { get; private set; } = DefaultPath;

    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepthView", "ablation");
    public static string DefaultPath => System.IO.Path.Combine(Folder, "ablation.json");
    public static string JobsFolder => System.IO.Path.Combine(Folder, "jobs");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The options every study file is read and written with (bundled seeds included).</summary>
    public static JsonSerializerOptions JsonOptions => Json;

    public static AblationDatabase Load(string? path = null)
    {
        path ??= DefaultPath;
        AblationDatabase db;
        try { db = File.Exists(path) ? JsonSerializer.Deserialize<AblationDatabase>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch { db = new(); }
        db.Path = path;
        // Never hand out a number a coupon already has, whatever the file says.
        int highest = db.Studies.SelectMany(s => s.Coupons).Select(c => c.Number).DefaultIfEmpty(0).Max();
        if (db.NextTestNumber <= highest) db.NextTestNumber = highest + 1;
        return db;
    }

    public void Save()
    {
        SavedUtc = DateTimeOffset.UtcNow;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// Starts over, for testing the feature: the old file is kept beside the new one as
    /// ablation-&lt;time&gt;.json (nothing is thrown away) and numbering restarts at 1.
    /// Physical coupons from before carry numbers the new database will hand out again, so
    /// this is for a test run, not for a database with real results in it.
    /// </summary>
    public string? Reset()
    {
        string? backup = null;
        if (File.Exists(Path))
        {
            backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $"ablation-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(Path, backup, overwrite: true);
        }
        Studies.Clear();
        NextTestNumber = 1;
        SeedsOfferedVersion = 0;   // the seeds are offered again on an empty database
        Save();
        return backup;
    }

    /// <summary>A coupon by test number: the local studies first (imported studies keep their own numbers; reference coupons have none).</summary>
    public (Study Study, Coupon Coupon)? FindCoupon(int number)
    {
        if (number <= 0) return null;
        foreach (var s in Studies.OrderBy(s => s.Reference ? 2 : s.Origin == "local" ? 0 : 1))
            foreach (var c in s.Coupons)
                if (c.Number == number) return (s, c);
        return null;
    }

    /// <summary>
    /// Plans the next coupon of a study: the next unassigned settings fill its squares in
    /// order, it gets the next test number, and the study records it. Null when the design
    /// is fully assigned or the coupon holds no squares.
    /// </summary>
    public Coupon? PlanCoupon(Study study)
    {
        if (study.Reference) return null;   // nothing is ever cut on a reference study
        var layout = study.Layout;
        if (layout.Capacity <= 0) return null;
        var todo = study.UnassignedDesign.Take(layout.Capacity).ToList();
        if (todo.Count == 0) return null;
        var coupon = new Coupon { Number = NextTestNumber++ };
        var cells = layout.SettingsCells.ToList();
        for (int i = 0; i < todo.Count; i++)
        {
            var cell = cells[i];
            coupon.Patches.Add(new Patch
            {
                Index = i, Row = cell.Row, Column = cell.Column, Label = cell.Label, DesignIndex = todo[i],
                Settings = study.Design[todo[i]],
                Reason = todo[i] < study.DesignReasons.Count ? study.DesignReasons[todo[i]] : "",
            });
        }
        study.Coupons.Add(coupon);
        return coupon;
    }

    /// <summary>
    /// Deletes a coupon record outright - evidence that was wrong, or a job result entered by
    /// mistake. Unlike retiring, nothing of it remains; its number is still never reused,
    /// because the counter only goes up. False when the coupon is not in the study.
    /// </summary>
    public static bool DeleteCoupon(Study study, Coupon coupon) => study.Coupons.Remove(coupon);

    /// <summary>
    /// Records the depth a real job reached: the evidence a person who engraves rather than
    /// cuts coupons can still give the advisor. The result goes into one local study per
    /// material and laser ("Jobs - Brass H62 on ..."), created on first use, as a coupon with
    /// its own test number and a single measured patch at the settings the job ran with. It
    /// counts as grade A - measured on this bench - exactly like a coupon patch, so the depth
    /// must be the depth at a full-depth area of the job after every layer ran.
    /// </summary>
    public (Study Study, Coupon Coupon) RecordJobResult(MaterialSpec material, LaserSpec laser, PatchSettings settings, PatchMeasurement measurement, string jobName, string notes)
    {
        string name = $"Jobs - {(string.IsNullOrEmpty(material.Alloy) ? material.Name : material.Summary)} on {laser.Name}";
        var study = Studies.FirstOrDefault(s => !s.Reference && s.Origin == "local" && s.Name == name
                                               && s.Material.Name == material.Name && s.Material.Alloy == material.Alloy && s.Laser.Id == laser.Id);
        if (study is null)
        {
            study = new Study
            {
                Name = name, Material = material, Laser = laser, Coupon = CouponSpec.Coin40,
                Notes = "Results of real jobs, one coupon record per job: the depth each reached at the settings it ran with. Entered by hand after measuring the piece; no design, nothing planned or cut from here.",
            };
            Studies.Add(study);
        }
        var coupon = new Coupon
        {
            Number = NextTestNumber++, EngravedUtc = DateTimeOffset.UtcNow,
            Notes = string.IsNullOrWhiteSpace(jobName) ? "job" : jobName.Trim(),
        };
        coupon.Patches.Add(new Patch
        {
            Index = 0, Row = 0, Column = 0, Label = "job", DesignIndex = -1, Settings = settings,
            Reason = "A real job, not a test patch: depth measured at a full-depth area after every layer ran." + (notes.Length > 0 ? " " + notes : ""),
            Status = PatchStatus.Measured, Measurement = measurement,
        });
        study.Coupons.Add(coupon);
        return (study, coupon);
    }

    /// <summary>Retires a coupon: its number is never used again and its settings go back to the pool.</summary>
    public bool RemoveCoupon(int number, string reason)
    {
        if (FindCoupon(number) is not { } found) return false;
        found.Coupon.Retired = true;
        found.Coupon.RetiredReason = reason;
        return true;
    }

    /// <summary>Records where a coupon's job was sent and which file it was; it is not engraved until the job ran.</summary>
    public static void MarkSent(Coupon coupon, double bedX, double bedY, string? jobFile)
    {
        coupon.BedCentreX = bedX;
        coupon.BedCentreY = bedY;
        coupon.JobFile = jobFile;
    }

    /// <summary>The job ran to the end: the coupon and its patches are engraved.</summary>
    public static void MarkEngraved(Coupon coupon)
    {
        coupon.EngravedUtc = DateTimeOffset.UtcNow;
        coupon.Patches = coupon.Patches.Select(p => p.Status == PatchStatus.Planned ? p with { Status = PatchStatus.Engraved } : p).ToList();
    }

    /// <summary>Keeps the exact job sent, so a coupon can always be traced to its G-code. Returns the path, or null if it could not be written.</summary>
    public static string? KeepJob(Study study, Coupon coupon, string gcode) => KeepJob($"DV{coupon.Number}", study, gcode);

    /// <summary>Keeps any job sent for a study under a name of its own ("focus ramp", say).</summary>
    public static string? KeepJob(string what, Study study, string gcode)
    {
        try
        {
            Directory.CreateDirectory(JobsFolder);
            var safe = string.Concat(study.Name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)).Trim();
            if (safe.Length == 0) safe = "study";
            var path = System.IO.Path.Combine(JobsFolder, $"{what} {safe} {DateTime.Now:yyyyMMdd-HHmmss}.gc");
            File.WriteAllText(path, gcode);
            return path;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- sharing

    /// <summary>One study as a file to share. The file names its schema so a later version can read it.</summary>
    public sealed class StudyFile
    {
        [JsonPropertyName("$schema")] public string SchemaName { get; set; } = "depthview.ablation-study/1";
        public string Program { get; set; } = $"DepthView {BuildInfo.Version}";
        public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string? ExportedBy { get; set; }
        public Study Study { get; set; } = new();
    }

    public static void Export(Study study, string path, string? exportedBy = null)
    {
        var f = new StudyFile { Study = study, ExportedBy = exportedBy };
        File.WriteAllText(path, JsonSerializer.Serialize(f, Json));
    }

    /// <summary>
    /// Brings a shared study in beside the local ones. It keeps its own test numbers (they
    /// are on that person's coupons, not these) and is marked with where it came from; a
    /// study with the same identity already here is replaced, so re-importing an updated
    /// file updates rather than duplicates.
    /// </summary>
    public Study Import(string path)
    {
        var f = JsonSerializer.Deserialize<StudyFile>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Not a DepthView study file.");
        if (!f.SchemaName.StartsWith("depthview.ablation-study/", StringComparison.Ordinal))
            throw new InvalidDataException($"Not a DepthView study file (schema {f.SchemaName}).");
        var s = f.Study;
        s.Origin = $"{System.IO.Path.GetFileName(path)}{(f.ExportedBy is null ? "" : $" from {f.ExportedBy}")}, exported {f.ExportedUtc.LocalDateTime:yyyy-MM-dd}";
        Studies.RemoveAll(x => x.Id == s.Id);
        Studies.Add(s);
        return s;
    }
}
