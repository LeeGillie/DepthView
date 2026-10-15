using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LumosUltra.Jobs;

namespace DepthView.Ablation;

// The ablation study: measured removal rates by material, alloy, laser and settings, kept so
// the simulator (TODO §7.6) is fitted to this machine rather than seeded with guesses.
//
// Vocabulary, used the same way everywhere:
//   study   - one material on one laser, one coupon size, one list of settings to test.
//   coupon  - one physical sheet or coin blank, identical in size to every other coupon of
//             the study. Carries a TEST NUMBER, engraved on it as a QR code and as digits, so
//             the measure phase knows which settings each patch was cut with. Numbers are
//             never reused: a removed coupon is retired and its settings go back to the pool.
//   patch   - one square on a coupon, cut with one setting for a known number of layers.
//   design  - the settings the study will test, assigned to coupons in order as each is planned.

/// <summary>The kind of source. Decides which settings exist (pulse width is MOPA only).</summary>
public enum LaserKind { Mopa, Uv }

/// <summary>
/// The laser a study was cut on. Power is recorded as a rating in watts, because "50 %" means
/// 30 W on a 60 W source and 50 W on a 100 W one: every figure derived from a setting uses
/// watts, and a setting carried to another rating is converted by watts (see
/// <see cref="PowerConversion"/>). The spot size is what the fluence figures assume; measure
/// it (TODO §7.1) rather than trusting the label.
/// </summary>
public sealed record LaserSpec
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public LaserKind Kind { get; init; }
    public double RatedPowerW { get; init; }
    public string Lens { get; init; } = "";
    public double SpotDiameterUm { get; init; }
    public double FieldMm { get; init; }
    public double FrequencyMinKHz { get; init; }
    public double FrequencyMaxKHz { get; init; }
    /// <summary>MOPA only: the discrete pulse widths the source offers, ns.</summary>
    public IReadOnlyList<double> PulseWidthsNs { get; init; } = [];
    public double SpeedMaxMmPerS { get; init; }
    public string Notes { get; init; } = "";

    public string Summary => $"{Name} - {RatedPowerW:0} W {Kind.ToString().ToUpperInvariant()}, {Lens}";

    /// <summary>
    /// Presets. The MOPA ranges come from the shared source profiles (nominal JPT figures, to be
    /// checked against MakeIt's limits); the UV rating is a placeholder to edit, because no UV
    /// Lumos has been characterised yet and its module ratings differ.
    /// </summary>
    public static IReadOnlyList<LaserSpec> Presets { get; } = BuildPresets();

    private static List<LaserSpec> BuildPresets()
    {
        var list = new List<LaserSpec>();
        foreach (var s in SourceProfile.Presets)
            list.Add(new LaserSpec
            {
                Id = s.Id, Name = s.Name, Kind = LaserKind.Mopa, RatedPowerW = s.RatedPowerW,
                Lens = LensProfile.Presets[0].Name, SpotDiameterUm = LensProfile.Presets[0].SpotDiameterUm,
                FieldMm = LensProfile.Presets[0].FieldSizeMm,
                FrequencyMinKHz = s.FrequencyMinKHz, FrequencyMaxKHz = s.FrequencyMaxKHz,
                PulseWidthsNs = s.PulseWidthsNs, SpeedMaxMmPerS = s.SpeedMaxMmPerS, Notes = s.Notes,
            });
        list.Add(new LaserSpec
        {
            Id = "lumos-ultra-uv", Name = "Lumos Ultra - UV (355 nm)", Kind = LaserKind.Uv, RatedPowerW = 5,
            Lens = "Lumos Ultra UV lens", SpotDiameterUm = 7, FieldMm = 110,
            FrequencyMinKHz = 20, FrequencyMaxKHz = 200, SpeedMaxMmPerS = 10000,
            Notes = "Rating and ranges are placeholders: edit them to your module's. Spot 6-8 um per WeCreat support.",
        });
        return list;
    }
}

/// <summary>
/// What was cut. Alloy matters: 60/40 and 70/30 brass do not remove alike, and "premium
/// brass" from a blank seller is whichever stamping brass the mill had. Density turns a
/// weighed mass loss into a mean depth (depth = mass / (density x area)).
/// </summary>
public sealed record MaterialSpec
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Grade or alloy designation, or "unknown" - never a guess dressed as a grade.</summary>
    public string Alloy { get; init; } = "unknown";
    public double DensityGPerCm3 { get; init; }
    public string Notes { get; init; } = "";

    public string Summary => Alloy is "" or "unknown" ? $"{Name} (alloy unknown)" : $"{Name} {Alloy}";

    public static IReadOnlyList<MaterialSpec> Presets { get; } =
    [
        new() { Id = "brass-h62", Name = "Brass", Alloy = "H62", DensityGPerCm3 = 8.4, Notes = "Chinese 60/40 stamping brass (near C280). The likely alloy of imported coin blanks." },
        new() { Id = "brass-c260", Name = "Brass", Alloy = "C260", DensityGPerCm3 = 8.53, Notes = "Cartridge brass, 70/30. The common US sheet alloy." },
        new() { Id = "brass-c280", Name = "Brass", Alloy = "C280", DensityGPerCm3 = 8.39, Notes = "Muntz metal, 60/40." },
        new() { Id = "brass-unknown", Name = "Brass", Alloy = "unknown", DensityGPerCm3 = 8.5, Notes = "Blanks sold as 'brass' with no grade. Ask the seller, or get an XRF reading." },
        new() { Id = "copper-c110", Name = "Copper", Alloy = "C110", DensityGPerCm3 = 8.94 },
        new() { Id = "bronze-unknown", Name = "Bronze", Alloy = "unknown", DensityGPerCm3 = 8.8 },
        new() { Id = "stainless-304", Name = "Stainless steel", Alloy = "304", DensityGPerCm3 = 7.93, Notes = "The one metal with a published removal efficiency to check against." },
        new() { Id = "stainless-316", Name = "Stainless steel", Alloy = "316", DensityGPerCm3 = 8.0 },
        new() { Id = "aluminium-6061", Name = "Aluminium", Alloy = "6061", DensityGPerCm3 = 2.70 },
        new() { Id = "titanium-gr2", Name = "Titanium", Alloy = "Grade 2", DensityGPerCm3 = 4.51 },
    ];
}

public enum CouponShape { Rectangle, Coin }

/// <summary>
/// The physical piece every coupon of a study is: a rectangle of sheet, or a round blank.
/// Every coupon of a study must be this exact size - the patch layout, the camera placement
/// and the probe route all assume it.
/// </summary>
public sealed record CouponSpec
{
    public CouponShape Shape { get; init; } = CouponShape.Rectangle;
    public double WidthMm { get; init; } = 101.6;
    public double HeightMm { get; init; } = 101.6;
    public double DiameterMm { get; init; } = 40;
    public double ThicknessMm { get; init; } = 3;
    /// <summary>Rounded corners of sheet stock, mm (0 = square).</summary>
    public double CornerRadiusMm { get; init; }

    /// <summary>The bounding box the layout works in.</summary>
    public double BoxWidthMm => Shape == CouponShape.Coin ? DiameterMm : WidthMm;
    public double BoxHeightMm => Shape == CouponShape.Coin ? DiameterMm : HeightMm;

    public string Summary => Shape == CouponShape.Coin
        ? $"{DiameterMm:0.#} mm coin blank, {ThicknessMm:0.#} mm thick"
        : $"{WidthMm:0.#} x {HeightMm:0.#} mm sheet, {ThicknessMm:0.#} mm thick";

    public static CouponSpec Sheet4Inch => new() { Shape = CouponShape.Rectangle, WidthMm = 101.6, HeightMm = 101.6, ThicknessMm = 3 };
    public static CouponSpec Coin40 => new() { Shape = CouponShape.Coin, DiameterMm = 40, ThicknessMm = 3 };
}

/// <summary>
/// One setting to test, in the units MakeIt shows (speed mm/s, line density per CENTIMETRE,
/// frequency kHz, pulse width ns), plus how many layers the patch is cut with. Layers are
/// cut at the same focus: the study measures removal per layer at a known defocus, and the
/// Z advance is a result (TODO §7.9), never an input.
/// </summary>
public sealed record PatchSettings
{
    public double PowerPercent { get; init; } = 100;
    public double SpeedMmPerS { get; init; } = 500;
    public double LineDensityPerCm { get; init; } = 300;
    public double FrequencyKHz { get; init; } = 45;
    /// <summary>MOPA only; 0 on UV.</summary>
    public double PulseWidthNs { get; init; } = 250;
    public int Layers { get; init; } = 10;
    /// <summary>The raster turns this much per layer (MakeIt's Auto Planning turns 27 degrees). 0 keeps every layer along X.</summary>
    public double HatchStepDeg { get; init; } = 27;
    /// <summary>Focus offset the coupon was cut at, mm above (+) or below (-) the surface, as set on the machine. Recorded, not commanded.</summary>
    public double FocusOffsetMm { get; init; }

    public double LinePitchMm => LineDensityPerCm > 0 ? 10.0 / LineDensityPerCm : double.NaN;

    /// <summary>Average power delivered, W, for a rating - assuming output is linear in the percentage (test T1 checks that).</summary>
    public double PowerW(double ratedW) => ratedW * PowerPercent / 100.0;

    /// <summary>Energy per unit area per layer, J/mm^2: P / (v x h). The dose the depth model works in.</summary>
    public double DosePerLayerJPerMm2(double ratedW) =>
        SpeedMmPerS > 0 && LinePitchMm > 0 ? PowerW(ratedW) / (SpeedMmPerS * LinePitchMm) : double.NaN;

    public double TotalDoseJPerMm2(double ratedW) => DosePerLayerJPerMm2(ratedW) * Math.Max(1, Layers);

    /// <summary>The shared physics for these settings on a laser (fluence, overlap...).</summary>
    public Physics PhysicsOn(LaserSpec laser) => Physics.From(ToMopa(), new SourceProfile
    {
        Id = laser.Id, Name = laser.Name, RatedPowerW = laser.RatedPowerW,
        FrequencyMinKHz = laser.FrequencyMinKHz, FrequencyMaxKHz = laser.FrequencyMaxKHz,
        PulseWidthsNs = laser.PulseWidthsNs, SpeedMaxMmPerS = laser.SpeedMaxMmPerS,
    }, new LensProfile { Id = laser.Id + "-lens", Name = laser.Lens, FieldSizeMm = laser.FieldMm, SpotDiameterUm = laser.SpotDiameterUm });

    public MopaSettings ToMopa() => new()
    {
        PowerPercent = PowerPercent, SpeedMmPerS = SpeedMmPerS, LineDensityPerCm = LineDensityPerCm,
        FrequencyKHz = FrequencyKHz, PulseWidthNs = PulseWidthNs, Passes = Math.Max(1, Layers), FocusOffsetMm = FocusOffsetMm,
    };

    public string ToShortString(LaserKind kind) =>
        $"P{PowerPercent:0.#}% v{SpeedMmPerS:0.#} d{LineDensityPerCm:0.#} f{FrequencyKHz:0.#}" +
        (kind == LaserKind.Mopa ? $" t{PulseWidthNs:0.#}" : "") + $" x{Layers}";

    /// <summary>Same cut, ignoring the recorded focus offset and hatch step.</summary>
    public bool SameCut(PatchSettings o) =>
        Math.Abs(PowerPercent - o.PowerPercent) < 1e-9 && Math.Abs(SpeedMmPerS - o.SpeedMmPerS) < 1e-9
        && Math.Abs(LineDensityPerCm - o.LineDensityPerCm) < 1e-9 && Math.Abs(FrequencyKHz - o.FrequencyKHz) < 1e-9
        && Math.Abs(PulseWidthNs - o.PulseWidthNs) < 1e-9 && Layers == o.Layers;
}

public enum PatchStatus { Planned, Engraved, Measured }

/// <summary>What the measure phase will record for a patch. Shape settled, entry not built (see AblationWindow).</summary>
public sealed record PatchMeasurement
{
    /// <summary>Depth readings inside the patch relative to the untouched margin beside it, micrometres.</summary>
    public IReadOnlyList<double> ReadingsUm { get; init; } = [];
    /// <summary>"gauge" (indicator on the CNC), "probe" (electrical touch probe), "mass" (weighed), "microscope".</summary>
    public string Method { get; init; } = "";
    public string Notes { get; init; } = "";
    public DateTimeOffset? MeasuredUtc { get; init; }

    public double? MeanDepthUm => ReadingsUm.Count > 0 ? ReadingsUm.Average() : null;
}

/// <summary>One square on a coupon.</summary>
public sealed record Patch
{
    public int Index { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Where the design put it: which entry of the study's design list (-1 for the ID mark).</summary>
    public int DesignIndex { get; init; } = -1;
    public PatchSettings Settings { get; init; } = new();
    public string Reason { get; init; } = "";
    public PatchStatus Status { get; init; }
    public PatchMeasurement? Measurement { get; init; }
}

/// <summary>A physical coupon with its test number. See the vocabulary at the top of this file.</summary>
public sealed class Coupon
{
    public int Number { get; set; }
    public List<Patch> Patches { get; set; } = [];
    public DateTimeOffset PlannedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EngravedUtc { get; set; }
    /// <summary>Bed position of the coupon's centre when it was sent, mm.</summary>
    public double? BedCentreX { get; set; }
    public double? BedCentreY { get; set; }
    /// <summary>The job file as sent, kept beside the database so a coupon can always be traced to its G-code.</summary>
    public string? JobFile { get; set; }
    /// <summary>Set when the coupon was removed: its number is never reused and its settings went back to the pool.</summary>
    public bool Retired { get; set; }
    public string? RetiredReason { get; set; }
    public string Notes { get; set; } = "";

    public string Status => Retired ? "retired" : EngravedUtc is null ? "planned" : Patches.Any(p => p.Status == PatchStatus.Measured) ? "measuring" : "engraved";
}

/// <summary>One material on one laser, with one coupon size and one list of settings to test.</summary>
public sealed class Study
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public MaterialSpec Material { get; set; } = MaterialSpec.Presets[0];
    public LaserSpec Laser { get; set; } = LaserSpec.Presets[0];
    public CouponSpec Coupon { get; set; } = CouponSpec.Sheet4Inch;
    public double PatchSizeMm { get; set; } = 5;
    public double GapMm { get; set; } = 1.5;
    public double MarginMm { get; set; } = 2;
    /// <summary>The settings that mark the test number: a dark mark, not depth. Starting point only; tune it to the material.</summary>
    public PatchSettings IdMark { get; set; } = new() { PowerPercent = 60, SpeedMmPerS = 400, LineDensityPerCm = 500, FrequencyKHz = 30, PulseWidthNs = 200, Layers = 1, HatchStepDeg = 0 };
    /// <summary>Everything the study means to test, in the order it will be cut.</summary>
    public List<PatchSettings> Design { get; set; } = [];
    public List<string> DesignReasons { get; set; } = [];
    public List<Coupon> Coupons { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Notes { get; set; } = "";
    /// <summary>"local", or where an imported study came from.</summary>
    public string Origin { get; set; } = "local";

    // Safety: no patch may receive more energy than could remove this share of the coupon's
    // thickness at a removal rate no metal here has shown. Both are study settings so a
    // measured rate can tighten them; the defaults are deliberately generous on the rate.
    public double DepthBudgetFraction { get; set; } = 0.5;
    public double AssumedMaxRemovalUmPerJPerMm2 { get; set; } = 10;

    /// <summary>The most energy any square millimetre of a coupon may receive, J/mm^2.</summary>
    public double EnergyCeilingJPerMm2 => Math.Max(1, DepthBudgetFraction * Coupon.ThicknessMm * 1000 / AssumedMaxRemovalUmPerJPerMm2);

    [JsonIgnore] public PatchLayout Layout => PatchLayout.For(Coupon, PatchSizeMm, GapMm, MarginMm);

    /// <summary>Design entries not yet on a live coupon.</summary>
    [JsonIgnore]
    public IEnumerable<int> UnassignedDesign
    {
        get
        {
            var used = Coupons.Where(c => !c.Retired).SelectMany(c => c.Patches).Select(p => p.DesignIndex).Where(i => i >= 0).ToHashSet();
            return Enumerable.Range(0, Design.Count).Where(i => !used.Contains(i));
        }
    }

    /// <summary>How many more coupons the remaining design needs.</summary>
    public int CouponsNeeded() => CouponsFor(UnassignedDesign.Count());

    public int CouponsFor(int settings) => Layout.Capacity <= 0 ? int.MaxValue : (settings + Layout.Capacity - 1) / Layout.Capacity;

    public string Summary => $"{Material.Summary} on {Laser.Name} - {Coupon.Summary} - {Design.Count} settings, {Coupons.Count(c => !c.Retired)} coupons";
}
