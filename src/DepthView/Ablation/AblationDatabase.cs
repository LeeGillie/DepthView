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
    public DateTimeOffset? SavedUtc { get; set; }

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
        Save();
        return backup;
    }

    public (Study Study, Coupon Coupon)? FindCoupon(int number)
    {
        foreach (var s in Studies)
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
    public static string? KeepJob(Study study, Coupon coupon, string gcode)
    {
        try
        {
            Directory.CreateDirectory(JobsFolder);
            var safe = string.Concat(study.Name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)).Trim();
            if (safe.Length == 0) safe = "study";
            var path = System.IO.Path.Combine(JobsFolder, $"DV{coupon.Number} {safe} {DateTime.Now:yyyyMMdd-HHmmss}.gc");
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
