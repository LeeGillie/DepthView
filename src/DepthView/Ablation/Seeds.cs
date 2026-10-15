using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DepthView.Ablation;

/// <summary>
/// What the literature says a material removes, per material: a starting point for someone
/// who cannot run the bench tests, shown with its grade so nobody mistakes it for a
/// measurement. The figures and grades are those of docs/research/laser-ablation-baselines.md
/// (Table R): stainless is the only metal with credible removal efficiencies, and they come
/// from other machines; brass, copper and aluminium have none, only a placeholder range
/// scaled from stainless by cold absorptivity; titanium and every UV figure are NONE.
/// Removal efficiency eta is in micrometres per J/mm² (1 mm³/J = 1000 µm per J/mm²).
/// </summary>
public sealed record MaterialPrior
{
    public string MaterialName { get; init; } = "";
    /// <summary>eta range, µm per J/mm² of area dose; null when the literature has no number at all.</summary>
    public double? EtaLow { get; init; }
    public double? EtaHigh { get; init; }
    public string Grade { get; init; } = "E";
    public string Basis { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Url { get; init; }

    /// <summary>The eta used to suggest layer counts: the high end, so a first coupon errs shallow rather than deep.</summary>
    public double SuggestEta => EtaHigh ?? 2.7;

    public string Summary => EtaLow is { } lo && EtaHigh is { } hi
        ? $"{MaterialName}: η {lo:0.##}–{hi:0.##} µm per J/mm² (grade {Grade}; {Basis})"
        : $"{MaterialName}: no removal figure in the literature (grade {Grade}; {Basis})";

    public static readonly string ResearchDoc = "docs/research/laser-ablation-baselines.md";

    public static MaterialPrior For(MaterialSpec m, LaserKind kind)
    {
        if (kind == LaserKind.Uv)
            return new() { MaterialName = m.Name, Grade = "E", Basis = "no measured UV removal rate for any metal; only an energy-balance ceiling", Source = ResearchDoc };
        return m.Name switch
        {
            "Stainless steel" => new()
            {
                MaterialName = m.Name, EtaLow = 0.7, EtaHigh = 2.7, Grade = "B/C",
                Basis = "Trotec's two 20 W, 200 ns recipes (about 2.5 at 60 kHz, about 0.7 at 180 kHz) and JMMP 2020 on 316L at 108 W (0.8–2.5, best 2.0); other machines, other spots",
                Source = "Trotec help centre; JMMP 2020 (MDPI 2504-4494/4/4/110)", Url = "https://www.mdpi.com/2504-4494/4/4/110",
            },
            "Brass" or "Bronze" => new()
            {
                MaterialName = m.Name, EtaLow = 0.1, EtaHigh = 2.7, Grade = "E",
                Basis = "NONE measured; placeholder 0.15–1.0 × stainless from cold absorptivity (0.052 vs 0.30 at 1064 nm). The '32 µm per pass' figure in circulation has no locatable source",
                Source = ResearchDoc,
            },
            "Copper" => new()
            {
                MaterialName = m.Name, EtaLow = 0.1, EtaHigh = 2.7, Grade = "E",
                Basis = "NONE measured; placeholder 0.15–1.0 × stainless. Cold absorptivity 0.03–0.045, rising once molten, so the first pulses couple worst",
                Source = ResearchDoc,
            },
            "Aluminium" => new()
            {
                MaterialName = m.Name, EtaLow = 0.2, EtaHigh = 2.7, Grade = "E",
                Basis = "NONE measured with settings; placeholder 0.25–1.0 × stainless. One forum report of about 8.5 µm per pass on 6061 at 60 W lacks speed, pitch and frequency",
                Source = ResearchDoc,
            },
            _ => new() { MaterialName = m.Name, Grade = "E", Basis = "no ns 1064 nm removal figure found", Source = ResearchDoc },
        };
    }
}

/// <summary>
/// The studies DepthView can seed an empty database with. Today they are literature priors:
/// reference studies whose patches are recipes other people reported, with the depth they
/// reported (status Predicted, never Measured) and the source, grade and machine on every
/// patch. Later releases add studies measured on a known bench, bundled as study files
/// (embedded resources named *.study.json), which then outrank the literature. A seed's
/// identity is fixed, so adding the seeds again replaces rather than duplicates.
/// </summary>
public static class Seeds
{
    /// <summary>Bumped when the bundled set changes, so a database that declined an older set is asked again.</summary>
    public const int Version = 1;

    public sealed record Seed(Study Study, string Kind, string From, string Grade, string What);

    public static IReadOnlyList<Seed> All()
    {
        var list = new List<Seed> { StainlessTrotec(), Stainless316Jmmp(), BrassPractitioners() };
        list.AddRange(Embedded());
        return list;
    }

    /// <summary>Bundled study files (none yet): measured studies from a known bench, added in later releases.</summary>
    private static IEnumerable<Seed> Embedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".study.json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            AblationDatabase.StudyFile? f;
            try { f = JsonSerializer.Deserialize<AblationDatabase.StudyFile>(stream, AblationDatabase.JsonOptions); }
            catch (JsonException) { continue; }
            if (f is null) continue;
            f.Study.Origin = $"bundled with DepthView{(f.ExportedBy is null ? "" : $", measured by {f.ExportedBy}")}";
            yield return new Seed(f.Study, "measured on another bench", f.ExportedBy ?? "bundled", "A (that bench)", $"{f.Study.Coupons.Sum(c => c.Patches.Count)} measured patches");
        }
    }

    private static Provenance Prov(string source, string url, string grade, string machine, string note) =>
        new() { Source = source, Url = url, Grade = grade, Machine = machine, Note = note };

    private static Patch ReportedPatch(int index, PatchLayout layout, PatchSettings s, string reason, Provenance prov, double[]? reportedDepthUm, string depthNote) =>
        new()
        {
            Index = index, Row = layout.Cells[index].Row, Column = layout.Cells[index].Column, Label = layout.Cells[index].Label,
            DesignIndex = index, Settings = s, Reason = reason, Status = PatchStatus.Predicted, Provenance = prov,
            Measurement = reportedDepthUm is null ? null : new PatchMeasurement { ReadingsUm = reportedDepthUm, Method = "reported by the source, not measured here", Notes = depthNote },
        };

    private static Study NewReference(Guid id, string name, MaterialSpec material, LaserSpec laser, string notes) =>
        new()
        {
            Id = id, Name = name, Material = material, Laser = laser, Coupon = CouponSpec.Sheet4Inch,
            Origin = "literature (bundled with DepthView)", Reference = true, Notes = notes,
            CreatedUtc = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
        };

    private static Coupon ReferenceCoupon(string label, IEnumerable<Patch> patches) =>
        new() { Number = 0, Label = label, Patches = patches.ToList(), PlannedUtc = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero) };

    // ------------------------------------------------------------ stainless, Trotec

    private static Seed StainlessTrotec()
    {
        var laser = new LaserSpec
        {
            Id = "lit-trotec-20w", Name = "Trotec SpeedMarker fibre, 20 W MOPA (literature)", Kind = LaserKind.Mopa, RatedPowerW = 20,
            Lens = "not stated", SpotDiameterUm = 0, FrequencyMinKHz = 20, FrequencyMaxKHz = 200, PulseWidthsNs = [200], SpeedMaxMmPerS = 3000,
            Notes = "Another maker's 20 W source. Spot, lens and focus not stated. Percent here is of 20 W.",
        };
        var material = MaterialSpec.Presets.First(m => m.Id == "stainless-304") with { Alloy = "unknown (stainless, grade not stated)" };
        var s = NewReference(new Guid("7a1d3c52-1e5b-4e7f-9d10-2c0f5a8b9e01"),
            "Stainless: Trotec deep-engraving recipes, 20 W (literature)", material, laser,
            "Two recipes from Trotec's help centre on deep engraving metal, with the depth they reached. " +
            "Grade C-derived: a vendor's measured figures with settings stated, on their own 20 W source. " +
            "Against the dose each recipe delivers (P/(v·h): 2.7 and 0.74 J/mm² per pass) they give η of about 2.2–2.8 µm per J/mm² " +
            "at 60 kHz and about 0.6–0.7 at 180 kHz - the same source, three to four times apart on pulse frequency alone. " +
            "Nothing here was cut on a Lumos Ultra.");
        var layout = s.Layout;
        const string url = "https://www.troteclaser.com/en-us/helpcenter/materials/material-usage-hints/deep-engraving-metal";
        var a = new PatchSettings { PowerPercent = 100, SpeedMmPerS = 250, LineDensityPerCm = 333, FrequencyKHz = 60, PulseWidthNs = 200, Layers = 32, HatchStepDeg = 0 };
        var b = new PatchSettings { PowerPercent = 100, SpeedMmPerS = 900, LineDensityPerCm = 333, FrequencyKHz = 180, PulseWidthNs = 200, Layers = 450, HatchStepDeg = 0 };
        var patches = new List<Patch>
        {
            ReportedPatch(0, layout, a, "Trotec recipe A: 60 kHz, 250 mm/s, 0.03 mm, 32 passes; up to 100 µm of warp reported",
                Prov("Trotec help centre: deep engraving metal", url, "C-derived", "Trotec 20 W fibre MOPA, lens not stated",
                     "Depth per pass 6.3–7.5 µm reported; the readings below are 32 passes at each end of that range."),
                [32 * 6.3, 32 * 7.5], "32 passes × 6.3–7.5 µm per pass, as reported"),
            ReportedPatch(1, layout, b, "Trotec recipe B: 180 kHz, 900 mm/s, 0.03 mm, 450 passes; no warp reported",
                Prov("Trotec help centre: deep engraving metal", url, "C-derived", "Trotec 20 W fibre MOPA, lens not stated",
                     "Depth per pass 0.44–0.49 µm reported; the readings below are 450 passes at each end of that range."),
                [450 * 0.44, 450 * 0.49], "450 passes × 0.44–0.49 µm per pass, as reported"),
        };
        s.Design = [a, b];
        s.DesignReasons = ["Trotec recipe A", "Trotec recipe B"];
        s.Coupons = [ReferenceCoupon("Trotec help centre", patches)];
        return new Seed(s, "literature", "Trotec help centre (vendor, 20 W source)", "C-derived", "2 recipes with reported depths");
    }

    // ------------------------------------------------------------ 316L, JMMP 2020

    private static Seed Stainless316Jmmp()
    {
        var laser = new LaserSpec
        {
            Id = "lit-jmmp-108w", Name = "108 W ns fibre, 38 µm spot (JMMP 2020, literature)", Kind = LaserKind.Mopa, RatedPowerW = 108,
            Lens = "not stated (38 µm spot)", SpotDiameterUm = 38, FrequencyMinKHz = 20, FrequencyMaxKHz = 500, PulseWidthsNs = [17, 60, 150, 280, 500], SpeedMaxMmPerS = 5000,
            Notes = "The paper's 108 W source. Percent here is of 108 W.",
        };
        var material = MaterialSpec.Presets.First(m => m.Id == "stainless-316") with { Alloy = "316L" };
        var s = NewReference(new Guid("7a1d3c52-1e5b-4e7f-9d10-2c0f5a8b9e02"),
            "Stainless 316L: JMMP 2020 best recipe, 108 W (literature)", material, laser,
            "From a peer-reviewed study of ns fibre deep engraving on 316L (J. Manuf. Mater. Process. 2020, 4(4), 110): " +
            "removal efficiency 0.77–2.5 µm per J/mm² across 17–500 ns, best 2.0 at 280–500 ns, with an area dose of 2.7–5.3 J/mm² " +
            "per layer and a 13.3 µm line pitch. Grade B-derived for the efficiency. The recipe's speed and frequency are NOT in " +
            "the paper as plain settings: they are reconstructed here from the stated dose and pitch, so use the η, not the recipe. " +
            "Nothing here was cut on a Lumos Ultra.");
        var layout = s.Layout;
        const string url = "https://www.mdpi.com/2504-4494/4/4/110";
        // 108 W at 100 %, pitch 13.3 µm (752 lines/cm), 4 J/mm² per layer -> v = P / (h × D) = 108 / (0.0133 × 4) ≈ 2030 mm/s.
        var best = new PatchSettings { PowerPercent = 100, SpeedMmPerS = 2030, LineDensityPerCm = 752, FrequencyKHz = 100, PulseWidthNs = 350, Layers = 1, HatchStepDeg = 0 };
        var patches = new List<Patch>
        {
            ReportedPatch(0, layout, best, "JMMP 2020 best recipe: 280–500 ns, 4 J/mm² per layer, 13.3 µm pitch; η ≈ 2.0 µm per J/mm²",
                Prov("JMMP 2020, 4(4), 110", url, "B-derived", "108 W ns fibre laser, 38 µm spot, lens not stated",
                     "Speed (2030 mm/s) and frequency (100 kHz) reconstructed from the stated dose and pitch; pulse width is the middle of the stated 280–500 ns band. The depth below is the stated efficiency × the dose: 4 J/mm² × 2.0 µm per J/mm² = 8 µm per layer."),
                [8.0], "1 layer × 8 µm, from the stated η and dose"),
        };
        s.Design = [best];
        s.DesignReasons = ["JMMP 2020 best recipe (reconstructed)"];
        s.Coupons = [ReferenceCoupon("JMMP 2020", patches)];
        return new Seed(s, "literature", "JMMP 2020 (peer-reviewed, 108 W source)", "B-derived", "1 recipe, η from the paper, settings reconstructed");
    }

    // ------------------------------------------------------------ brass, practitioners

    private static Seed BrassPractitioners()
    {
        var laser = LaserSpec.Presets.FirstOrDefault(l => Math.Abs(l.RatedPowerW - 60) < 1 && l.Kind == LaserKind.Mopa) ?? LaserSpec.Presets[0];
        laser = laser with { Id = "lit-60w-mopa", Name = "60 W MOPA, maker not stated (practitioners, literature)", Lens = "not stated", Notes = "Recipes from forum and blog posts; the exact source and lens are not stated. Percent here is of 60 W." };
        var material = MaterialSpec.Presets.First(m => m.Id == "brass-unknown");
        var s = NewReference(new Guid("7a1d3c52-1e5b-4e7f-9d10-2c0f5a8b9e03"),
            "Brass: coin-relief recipes from practitioners, 60 W (literature, no depths)", material, laser,
            "Two coin recipes from ComMarker's blog, as settings people used - NO depth was measured in either, so they are " +
            "starting points for a design, not removal figures (grade E). The literature has no measured removal efficiency " +
            "for brass at all: the '32 µm per pass' figure attributed to a Raycus 100 W note has no locatable source, and a " +
            "LightBurn forum post reports that a 30 W source at 1000 mm/s for 128 passes produced only surface etching on brass coins. " +
            "If brass behaved like stainless (η ≈ 2.5), recipe A's 1.2 J/mm² per layer would give about 3 µm per layer and about 1.2 mm " +
            "in 400 layers - arithmetic, not a measurement. Nothing here was cut on a Lumos Ultra.");
        var layout = s.Layout;
        const string urlA = "https://blog.commarker.com/archives/56496";
        var a = new PatchSettings { PowerPercent = 80, SpeedMmPerS = 1000, LineDensityPerCm = 250, FrequencyKHz = 30, PulseWidthNs = 200, Layers = 400, HatchStepDeg = 0 };
        var b = new PatchSettings { PowerPercent = 95, SpeedMmPerS = 2000, LineDensityPerCm = 400, FrequencyKHz = 100, PulseWidthNs = 200, Layers = 256, HatchStepDeg = 0 };
        var patches = new List<Patch>
        {
            ReportedPatch(0, layout, a, "ComMarker coin recipe A: 60 W, 1000 mm/s, 80 %, 30 kHz, 200 ns, 0.04 mm, 400 layers - no depth reported",
                Prov("ComMarker blog, deep engraving coins", urlA, "E", "60 W MOPA, lens not stated", "A recipe with no measured depth."), null, ""),
            ReportedPatch(1, layout, b, "ComMarker coin recipe B: 2000 mm/s, 95 %, 100 kHz, 200 ns, 0.025 mm, 256 layers, with a cleanup pass every 10 layers at 30 % and 80 kHz - no depth reported",
                Prov("ComMarker blog, deep engraving coins", urlA, "E", "60 W MOPA, lens not stated", "A recipe with no measured depth; the cleanup pass is not represented in the settings."), null, ""),
        };
        s.Design = [a, b];
        s.DesignReasons = ["ComMarker recipe A", "ComMarker recipe B"];
        s.Coupons = [ReferenceCoupon("ComMarker blog", patches)];
        return new Seed(s, "literature", "ComMarker blog (practitioner recipes, 60 W)", "E", "2 recipes, no depths");
    }

    /// <summary>Adds every seed to the database, replacing an earlier copy of the same seed. Returns how many were added.</summary>
    public static int AddAll(AblationDatabase db)
    {
        int n = 0;
        foreach (var seed in All())
        {
            db.Studies.RemoveAll(x => x.Id == seed.Study.Id);
            db.Studies.Add(seed.Study);
            n++;
        }
        db.SeedsOfferedVersion = Version;
        return n;
    }
}
