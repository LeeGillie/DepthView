using System;
using System.Collections.Generic;
using System.Linq;

namespace DepthView.Materials;

/// <summary>The kind of source, by wavelength: what decides whether a material couples at all.</summary>
public enum SourceKind
{
    /// <summary>1064 nm: MOPA or Q-switched fibre.</summary>
    Fibre,
    /// <summary>355 nm.</summary>
    Uv,
    /// <summary>10.6 um.</summary>
    Co2,
    /// <summary>About 450 nm.</summary>
    Diode,
}

/// <summary>What a source can do to a material, for engraving depth from a map - not for marking.</summary>
public enum Capability
{
    /// <summary>Removes material in depth; the ordinary pairing.</summary>
    Works,
    /// <summary>Removes material, but slowly, unevenly or with a floor that is not the material: say so before cutting.</summary>
    Marginal,
    /// <summary>Does not remove the material, or should not be pointed at it. Refused with the reason.</summary>
    Cannot,
}

/// <summary>
/// One material as the whole program knows it. The relief preview, the finishing simulator
/// and the ablation study each keep their own list with their own fields (a look, a chemistry,
/// a density); this is the one name that ties them together, so the Blank panel asks once.
/// </summary>
public sealed record CatalogMaterial(
    string Id,
    string Name,
    string Family,
    bool Metal,
    string? ReliefPreset,   // name of the Rendering.MaterialPreset that looks like it; null leaves the relief look alone
    string? FinishId,       // id in the finishing catalogue; null when finishing has nothing for it
    string? AblationName,   // the Ablation.MaterialSpec name the study and the advisor use; null when none is kept
    string Notes)
{
    public override string ToString() => Name;
}

public sealed record CapabilityVerdict(Capability Level, string Reason)
{
    public bool Refused => Level == Capability.Cannot;
    public string Word => Level switch { Capability.Works => "works", Capability.Marginal => "marginal", _ => "not possible" };
}

/// <summary>
/// The materials, and what each kind of source can do to them. The capability table is
/// physics everyone in the trade knows - which wavelengths a material absorbs, reflects or
/// transmits - stated in words, with no removal figure anywhere: figures come only from the
/// ablation study's evidence. A wrong pairing is refused with its reason rather than
/// advised with a number.
/// </summary>
public static class MaterialCatalog
{
    public const string DefaultId = "brass";

    public static IReadOnlyList<CatalogMaterial> All { get; } =
    [
        new("brass", "Brass", "copper alloy", true, "Polished brass", "brass", "Brass",
            "Coin blanks, plates, tags. 60/40 and 70/30 alloys remove differently; the study records the alloy."),
        new("bronze", "Bronze", "copper alloy", true, "Polished brass", "bronze", "Bronze",
            "Tin bronze; behaves like brass under the beam, darker when finished."),
        new("copper", "Copper", "copper alloy", true, "Copper", "copper", "Copper",
            "The most reflective and conductive of the common metals at 1064 nm: couples worst while cold."),
        new("stainless", "Stainless steel", "steel", true, "Stainless steel", "stainless", "Stainless steel",
            "The one metal with a published removal efficiency to check a study against."),
        new("mild-steel", "Mild steel", "steel", true, "Brushed stainless", null, null,
            "Carbon steel; absorbs well, rusts where cut unless sealed. Not yet in the study's material list."),
        new("aluminium", "Aluminium", "light metal", true, "Brushed stainless", null, "Aluminium",
            "Bare aluminium: reflective, conductive, oxidises grey where cut."),
        new("aluminium-anodised", "Anodised aluminium", "light metal", true, "Black anodised aluminium", null, null,
            "A coloured oxide layer a few microns thick over bare aluminium."),
        new("titanium", "Titanium", "reactive metal", true, "Stainless steel", "titanium", "Titanium",
            "Absorbs well; colours by oxide thickness, which a relief job will show at every depth."),
        new("silver", "Silver", "precious metal", true, "Stainless steel", null, null,
            "Highly reflective at 1064 nm; slow to couple. Not in the study's material list."),
        new("gold", "Gold", "precious metal", true, "Polished brass", null, null,
            "Reflects about 98 % of 1064 nm; the hardest common metal to couple. Not in the study's material list."),
        new("wood", "Wood", "organic", false, "Cherrywood", null, null,
            "Any species; a relief in wood is cut by charring and the floor is char-coloured."),
        new("leather", "Leather", "organic", false, "Cherrywood", null, null,
            "Vegetable-tanned leather engraves; chrome-tanned leather gives off chlorine compounds when cut."),
        new("acrylic", "Acrylic, opaque", "plastic", false, null, null, null,
            "Cast or extruded PMMA in an opaque colour."),
        new("acrylic-clear", "Acrylic, clear", "plastic", false, null, null, null,
            "Transparent PMMA: what passes through it matters more than what it absorbs."),
        new("slate", "Slate or stone", "mineral", false, "Slate", null, null,
            "Spalls to a light mark; depth comes slowly and unevenly."),
        new("glass", "Glass", "mineral", false, null, null, null,
            "Transparent to 1064 nm and to visible light; absorbs 10.6 um and 355 nm at the surface."),
    ];

    /// <summary>By id, or loosely by name ("stainless 304", "Brass (C260)"); the default when nothing matches.</summary>
    public static CatalogMaterial Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return All[0];
        string key = idOrName.Trim();
        return All.FirstOrDefault(m => m.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(m => key.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase) || m.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(m => key.Contains(m.Id, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(m => m.AblationName is { } a && (key.StartsWith(a, StringComparison.OrdinalIgnoreCase) || a.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
            ?? All[0];
    }

    /// <summary>The catalogue entry for an ablation study material, by its name; null when the study knows a material the catalogue does not.</summary>
    public static CatalogMaterial? ByAblationName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(m => m.AblationName is { } a && a.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The source kind for the Tune window's laser ids ("uv", "mopa", "fiber", "co2", "diode"); null when not set.</summary>
    public static SourceKind? SourceFromId(string? laserTypeId) => laserTypeId?.ToLowerInvariant() switch
    {
        "uv" => SourceKind.Uv,
        "mopa" or "fiber" or "fibre" => SourceKind.Fibre,
        "co2" => SourceKind.Co2,
        "diode" => SourceKind.Diode,
        _ => null,
    };

    public static SourceKind SourceFrom(Ablation.LaserKind kind) => kind == Ablation.LaserKind.Uv ? SourceKind.Uv : SourceKind.Fibre;

    public static string SourceName(SourceKind s) => s switch
    {
        SourceKind.Fibre => "a 1064 nm fibre source",
        SourceKind.Uv => "a 355 nm UV source",
        SourceKind.Co2 => "a CO2 source",
        _ => "a diode source",
    };

    /// <summary>
    /// Whether this source can engrave this material in depth, and why. The reasons are the
    /// optics of the pairing, not measurements: a measurement is what the ablation study is for.
    /// </summary>
    public static CapabilityVerdict Gate(SourceKind source, CatalogMaterial m)
    {
        switch (source)
        {
            case SourceKind.Fibre:
                switch (m.Id)
                {
                    case "brass" or "bronze":
                        return new(Capability.Works, "1064 nm couples to copper alloys poorly while they are cold (absorptivity of a few percent) and far better once the surface is molten or oxidised, so the first passes remove least. Deep relief in brass is routine on a fibre source.");
                    case "copper":
                        return new(Capability.Works, "Copper reflects most of 1064 nm cold and conducts the heat away; removal starts slowly and improves once the floor oxidises. Expect more passes than brass for the same depth.");
                    case "stainless" or "mild-steel" or "titanium":
                        return new(Capability.Works, "Steels and titanium absorb 1064 nm well: the ordinary fibre-laser metals.");
                    case "aluminium":
                        return new(Capability.Works, "Aluminium is reflective and conductive but removes well once coupled; the floor oxidises grey.");
                    case "aluminium-anodised":
                        return new(Capability.Marginal, "The anodised layer is a few microns thick and comes off in the first pass; below it the relief is cut in bare aluminium, so the colour changes wherever the map goes below the surface.");
                    case "silver" or "gold":
                        return new(Capability.Marginal, $"{m.Name} reflects nearly all of 1064 nm; removal is slow and the first passes couple worst. Expect many passes at high power, and no figure from the study until one is measured.");
                    case "wood" or "leather":
                        return new(Capability.Marginal, "1064 nm chars organics rather than vaporising them cleanly: a fibre source cuts a shallow relief with a charcoal floor. A CO2 or diode source is the usual tool for depth in wood and leather.");
                    case "acrylic":
                        return new(Capability.Marginal, "Opaque acrylic melts under 1064 nm rather than vaporising: a shallow relief with a raised melt lip.");
                    case "acrylic-clear" or "glass":
                        return new(Capability.Cannot, $"{m.Name} is transparent to 1064 nm: the beam passes through and heats whatever is under it, and glass can fracture at the focus. Nothing is engraved.");
                    case "slate":
                        return new(Capability.Marginal, "Stone spalls to a light mark under 1064 nm; depth comes slowly and unevenly, and the floor is rough.");
                }
                break;

            case SourceKind.Uv:
                if (m.Metal)
                    return new(Capability.Marginal, "355 nm is absorbed by nearly everything and marks metal cleanly with little heat, but removal per pass is tiny: a cold mark, not a deep relief. No measured UV removal rate exists for any metal; the study would be the first.");
                return m.Id switch
                {
                    "glass" or "acrylic-clear" => new(Capability.Marginal, "355 nm is absorbed at the surface of glass and clear acrylic, so it marks where 1064 nm would pass through; depth is slow and the floor frosted."),
                    _ => new(Capability.Marginal, "355 nm marks organics and plastics cleanly with little heat, and removes little per pass: a mark, not a relief, unless many passes are affordable."),
                };

            case SourceKind.Co2:
                if (m.Id == "aluminium-anodised")
                    return new(Capability.Marginal, "10.6 um marks anodised aluminium by bleaching the dyed layer; it removes no metal, so there is no depth below the layer.");
                if (m.Metal)
                    return new(Capability.Cannot, $"Bare {m.Name.ToLowerInvariant()} reflects 10.6 um: a CO2 source marks metal only through a marking compound or a coating, and removes no metal. A depth map has nothing to cut with.");
                return m.Id switch
                {
                    "glass" => new(Capability.Marginal, "10.6 um is absorbed at the surface of glass and frosts it by micro-fracture; there is no clean depth, only a frosted mark."),
                    "slate" => new(Capability.Marginal, "Stone spalls to a light mark under 10.6 um; depth comes slowly and the floor is rough."),
                    _ => new(Capability.Works, $"{m.Name} absorbs 10.6 um and vaporises cleanly: the CO2 source's home ground, and depth follows passes."),
                };

            case SourceKind.Diode:
                if (m.Id == "aluminium-anodised")
                    return new(Capability.Marginal, "A diode marks anodised aluminium by bleaching the dyed layer; it removes no metal.");
                if (m.Metal)
                    return new(Capability.Cannot, $"A diode's few watts at about 450 nm do not remove {m.Name.ToLowerInvariant()}; stainless can be colour-marked with heat, but nothing is engraved in depth.");
                return m.Id switch
                {
                    "wood" or "leather" => new(Capability.Works, $"{m.Name} absorbs 450 nm: the diode's home ground. Depth comes with passes, with a charred floor."),
                    "acrylic-clear" or "glass" => new(Capability.Cannot, $"{m.Name} is transparent at 450 nm: the beam passes through. Nothing is engraved."),
                    "acrylic" => new(Capability.Marginal, "Opaque acrylic absorbs 450 nm only in dark colours; light colours reflect most of it. Shallow at best."),
                    "slate" => new(Capability.Marginal, "Stone takes a light mark from a diode; depth is slow and uneven."),
                    _ => new(Capability.Marginal, $"{m.Name} marks under a diode; depth is slow."),
                };
        }
        return new(Capability.Marginal, $"No statement is kept for {m.Name.ToLowerInvariant()} on {SourceName(source)}; cut a test piece before trusting a depth map to it.");
    }
}
