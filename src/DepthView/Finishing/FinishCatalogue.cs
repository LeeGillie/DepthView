using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepthView.Finishing;

/// <summary>A link to where a number or a piece of advice came from.</summary>
public sealed class FinishSource
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Fields every catalogue entry shares: what it is, why, and where that came from.</summary>
public abstract class FinishEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>V (from a source), C (calculated), E (estimate), as free text per entry.</summary>
    public string Basis { get; set; } = "";
    public string Advice { get; set; } = "";
    public List<FinishSource> Sources { get; set; } = new();

    public override string ToString() => Name;
}

public sealed class FinishMaterial : FinishEntry
{
    public string F0 { get; set; } = "#F5E4AE";
    public string F0Basis { get; set; } = "";
    public double StockRoughness { get; set; } = 0.1;
    public double LaserFloorRoughness { get; set; } = 0.8;
    public string RoughnessBasis { get; set; } = "";
    public string Oxide { get; set; } = "#2B241E";
    public string? PickleTint { get; set; }
    public string ColourBasis { get; set; } = "";
}

public sealed class FinishCleaning : FinishEntry
{
    /// <summary>Fraction of the laser oxide left on the engraved area afterwards.</summary>
    public double OxideLeft { get; set; } = 1;
    /// <summary>A tool from the tools list that burnishes the bare metal as part of cleaning.</summary>
    public string? BurnishTool { get; set; }
    public double BurnishMinutes { get; set; }
    /// <summary>How far formerly oxidised areas shift toward the material's pickle tint.</summary>
    public double Pickle { get; set; }
    /// <summary>When set, every surface ends up at this roughness (a blast).</summary>
    public double? AllRoughness { get; set; }
}

public sealed class FinishPrepolish : FinishEntry
{
    public string? Tool { get; set; }
    public double Minutes { get; set; }
}

public sealed class FinishHazard
{
    public string Badge { get; set; } = "";
    /// <summary>danger, warning or caution.</summary>
    public string Level { get; set; } = "caution";
    public string Text { get; set; } = "";
}

public sealed class FinishDarkener : FinishEntry
{
    public string Maker { get; set; } = "";
    public List<string> Materials { get; set; } = new();

    /// <summary>Colour as darkness runs from 0 to 1, at full strength and well diluted.</summary>
    public List<string> Stops { get; set; } = new();
    public List<string> DiluteStops { get; set; } = new();

    /// <summary>Minutes for the reaction to go about two-thirds of the way at full strength.</summary>
    public double TauMinutes { get; set; } = 1;
    public double DefaultStrength { get; set; } = 1;
    public double DefaultMinutes { get; set; } = 1;
    public double MaxMinutes { get; set; } = 10;

    public double Roughness { get; set; } = 0.5;
    public double F0 { get; set; } = 0.04;

    /// <summary>Reaction-rate multiplier on brass, for products that barely react with it.</summary>
    public double HoldOnBrass { get; set; } = 1;
    /// <summary>How easily relieving takes it off; above 1 is softer than the selenium blacks.</summary>
    public double Durability { get; set; } = 1;
    /// <summary>Exposure (minutes x strength / tau) past which full strength goes crusty.</summary>
    public double Overdo { get; set; } = 99;

    public FinishHazard? Hazard { get; set; }

    /// <summary>
    /// A thin interference film (heat tint, anodizing) rather than a dark patina: its colour is
    /// the point, so "too light" is not a fault, and it shifts with the viewing angle.
    /// </summary>
    public bool Film { get; set; }

    [JsonIgnore] public bool IsNone => Stops.Count == 0;
}

public sealed class FinishTool : FinishEntry
{
    /// <summary>Probe radius; null is a plane (a flat block).</summary>
    public double? RadiusMm { get; set; }
    /// <summary>How far below the local envelope the tool still makes contact.</summary>
    public double ReachMm { get; set; }
    /// <summary>Patina removed per unit dwell where it touches, 0..1.</summary>
    public double Cut { get; set; }
    /// <summary>Roughness of the bare metal it leaves.</summary>
    public double BareRoughness { get; set; } = 0.3;
    public bool Brushed { get; set; }

    [JsonIgnore] public bool IsNone => Cut <= 0;
}

public sealed class FinishPressure
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Factor { get; set; } = 1;
    public override string ToString() => Name;
}

public sealed class FinishSealer : FinishEntry
{
    /// <summary>How far the patina is taken back toward lighter (wax: about one colour).</summary>
    public double Lighten { get; set; }
    public double RoughDelta { get; set; }
    /// <summary>1 lays a clear coat over everything.</summary>
    public double Clear { get; set; }
    public double ClearRoughness { get; set; }
}

public sealed class FinishNote
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Everything the finishing preview knows, read from the catalogue.json compiled into the
/// executable. Data rather than code so the numbers - most of them estimates waiting to be
/// calibrated against real test coins - can be corrected without touching the simulation,
/// and so every number travels with its basis and its source.
/// </summary>
public sealed class FinishCatalogue
{
    public const string Schema = "depthview.finishing-catalogue/1";
    private const string ResourceName = "DepthView.Finishing.catalogue.json";

    [JsonPropertyName("schema")] public string SchemaId { get; set; } = "";
    public string About { get; set; } = "";
    public List<FinishMaterial> Materials { get; set; } = new();
    public List<FinishCleaning> Cleaning { get; set; } = new();
    public List<FinishPrepolish> Prepolish { get; set; } = new();
    public List<FinishDarkener> Darkeners { get; set; } = new();
    public List<FinishTool> Tools { get; set; } = new();
    public List<FinishPressure> Pressures { get; set; } = new();
    public List<FinishSealer> Sealers { get; set; } = new();
    public List<FinishNote> General { get; set; } = new();

    private static FinishCatalogue? _builtin;

    /// <summary>The catalogue shipped in this build. Throws only if the build itself is broken.</summary>
    public static FinishCatalogue Builtin => _builtin ??= LoadBuiltin();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static FinishCatalogue LoadBuiltin()
    {
        using var s = typeof(FinishCatalogue).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new InvalidOperationException("The finishing catalogue is missing from this build.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static FinishCatalogue Parse(string json)
    {
        var c = JsonSerializer.Deserialize<FinishCatalogue>(json, Json)
                ?? throw new InvalidDataException("Empty finishing catalogue.");
        if (c.SchemaId != Schema)
            throw new InvalidDataException($"Finishing catalogue schema '{c.SchemaId}', expected '{Schema}'.");
        if (c.Materials.Count == 0 || c.Cleaning.Count == 0 || c.Darkeners.Count == 0
            || c.Tools.Count == 0 || c.Sealers.Count == 0 || c.Pressures.Count == 0)
            throw new InvalidDataException("Finishing catalogue is missing a section.");
        return c;
    }

    public FinishMaterial Material(string? id) => Find(Materials, id);
    public FinishCleaning Clean(string? id) => Find(Cleaning, id);
    public FinishPrepolish Pre(string? id) => Find(Prepolish, id);
    public FinishDarkener Darkener(string? id) => Find(Darkeners, id);
    public FinishTool Tool(string? id) => Find(Tools, id);
    public FinishSealer Sealer(string? id) => Find(Sealers, id);

    public FinishPressure Pressure(string? id)
        => Pressures.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? Pressures.FirstOrDefault(p => p.Id == "medium") ?? Pressures[0];

    /// <summary>The darkeners that make sense on a material, in catalogue order.</summary>
    public IEnumerable<FinishDarkener> DarkenersFor(string materialId)
        => Darkeners.Where(d => d.IsNone || d.Materials.Contains(materialId, StringComparer.OrdinalIgnoreCase));

    /// <summary>By id, case-insensitively; an unknown id falls back to the first entry.</summary>
    private static T Find<T>(List<T> list, string? id) where T : FinishEntry
        => list.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) ?? list[0];

    // ------------------------------------------------------------------ colour helpers

    /// <summary>sRGB hex to linear RGB.</summary>
    public static (double R, double G, double B) Linear(string hex)
    {
        var (r, g, b) = Srgb(hex);
        return (ToLinear(r), ToLinear(g), ToLinear(b));
    }

    public static (double R, double G, double B) Srgb(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return (0.5, 0.5, 0.5);
        int v = Convert.ToInt32(s, 16);
        return (((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
    }

    public static double ToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}
