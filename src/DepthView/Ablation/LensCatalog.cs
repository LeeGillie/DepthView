using System;
using System.Collections.Generic;
using System.Linq;
using LumosUltra.Jobs;

namespace DepthView.Ablation;

/// <summary>
/// One lens the user can name, wherever a lens is asked for. The Lumos Ultra's own lenses
/// come first and are the default; the common third-party f-theta sizes follow, and the last
/// choice is a lens typed by hand. The name is what is stored - in the profile, in the focus
/// record, in the study - so a lens chosen here and one typed in an earlier version match.
/// </summary>
public sealed record LensChoice(string Name, string Group, double FieldMm, double? SpotUm, string Note)
{
    public bool ShipsWithLumos => Group == Lumos;
    public const string Lumos = "Lumos Ultra, as shipped", ThirdParty = "Third-party f-theta", Other = "Other";

    /// <summary>How the choice reads in a list: the name, the field, and whether it came with the machine.</summary>
    public string Label => Group == Other ? Name
        : $"{Name} - {FieldMm:0} x {FieldMm:0} mm" + (SpotUm is { } s ? $", {s:0} um spot" : "") + (ShipsWithLumos ? " (as shipped)" : " (third party)");
}

public static class LensCatalog
{
    /// <summary>The one a Lumos Ultra MOPA comes with.</summary>
    public static LensChoice Default => All[0];

    /// <summary>The lens for a kind of source: the shipped UV lens for UV, the shipped red lens otherwise.</summary>
    public static LensChoice DefaultFor(LaserKind kind) => kind == LaserKind.Uv ? All[1] : All[0];

    public static readonly string OtherName = "Other lens (type it)";

    public static IReadOnlyList<LensChoice> All { get; } = Build();

    private static List<LensChoice> Build()
    {
        var list = new List<LensChoice>();
        // The shared library's presets first: today the red MOPA lens (field and spot nominal,
        // to be measured by T0); the UV lens as WeCreat support described it.
        foreach (var p in LensProfile.Presets)
            list.Add(new(p.Name, LensChoice.Lumos, p.FieldSizeMm, p.SpotDiameterUm > 0 ? p.SpotDiameterUm : null,
                $"Fitted to the MOPA Lumos Ultra as sold. Field and spot are {p.Evidence.ToString().ToLowerInvariant()} figures: test T0 (the focus ramp) measures the spot."));
        list.Add(new("Lumos Ultra UV lens", LensChoice.Lumos, 110, 7, "Fitted to the UV Lumos Ultra as sold. Spot 6-8 um per WeCreat support; field 110 mm."));
        // The common f-theta sizes people fit instead. The field is the maker's usual figure
        // for the focal length; the spot depends on the source's beam and is not known here.
        foreach (var (f, field) in new[] { (100, 70), (160, 110), (210, 150), (254, 175), (330, 220), (420, 300) })
            list.Add(new($"F{f} f-theta", LensChoice.ThirdParty, field, null,
                $"A third-party F{f} f-theta lens, about {field} x {field} mm field. Not supplied with the machine: MakeIt's field calibration and the study's spot size do not carry over, and the focus has to be found again for it."));
        list.Add(new(OtherName, LensChoice.Other, 0, null, "A lens not listed: name it as you would in your notes. Removal efficiency is calibrated per lens, so a study cut with it is a study of that lens."));
        return list;
    }

    /// <summary>The listed lens with this name, or null for a hand-typed one.</summary>
    public static LensChoice? Find(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(l => l.Group != LensChoice.Other && l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}
