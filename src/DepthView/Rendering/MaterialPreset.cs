using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DepthView.Rendering;

/// <summary>
/// A surface model for the relief preview.
///
/// Three things make this read as real laser work rather than a tinted grey image.
/// First, metals tint their *specular* reflection and have almost no diffuse, which is the
/// opposite of wood. Second, the engraved floor has a different finish from the untouched
/// field: brass goes from polished to frosted and darker, while black anodising and slate
/// go the other way and come out brighter than the surface around them. Third, real
/// surfaces have texture - grain in wood, scratches in brushed metal - and that texture
/// belongs to the *field*, because the laser destroys it in the engraved floor.
/// </summary>
public sealed class MaterialPreset
{
    public string Name { get; set; } = "";

    /// <summary>Metals tint specular and have near-zero diffuse; dielectrics are the reverse.</summary>
    public bool Metallic { get; set; }

    public double AlbedoR { get; set; } = 0.5;
    public double AlbedoG { get; set; } = 0.5;
    public double AlbedoB { get; set; } = 0.5;

    public double SpecR { get; set; } = 1.0;
    public double SpecG { get; set; } = 1.0;
    public double SpecB { get; set; } = 1.0;

    /// <summary>Phong exponent on the untouched surface and in the engraved floor.</summary>
    public double FieldGloss { get; set; } = 90;
    public double EngravedGloss { get; set; } = 12;

    /// <summary>Specular strength on the untouched surface and in the engraved floor.</summary>
    public double FieldSpec { get; set; } = 1.0;
    public double EngravedSpec { get; set; } = 0.4;

    /// <summary>
    /// Albedo multiplier at full depth. Below 1 the engraving darkens (brass oxide, charred
    /// wood); above 1 it lightens (black anodising ablated to bare aluminium, slate to white).
    /// </summary>
    public double EngravedTone { get; set; } = 0.5;

    /// <summary>How much of the surrounding environment the surface reflects.</summary>
    public double EnvStrength { get; set; } = 0.4;

    public double Ambient { get; set; } = 0.15;

    // ------------------------------------------------------------------ texture

    /// <summary>Image supplying surface colour: a photo of oak, slate, leather.</summary>
    public string? AlbedoTexturePath { get; set; }

    /// <summary>
    /// Image read as a fine height field that perturbs the surface normal. This is what makes
    /// brushed metal look brushed, hammered copper look hammered, and canvas look woven.
    /// </summary>
    public string? MicroTexturePath { get; set; }

    /// <summary>
    /// Built-in generated texture: "brushed", "wood", "speckle", or null for none.
    /// Generated textures only fill slots no image file has been supplied for, so pointing a
    /// material at a photograph always wins. They exist so that a wood preset is actually
    /// wooden out of the box rather than a flat brown card waiting for you to find a photo.
    /// </summary>
    public string? ProceduralTexture { get; set; }

    /// <summary>How many times the texture repeats across the width of the workpiece.</summary>
    public double TextureScale { get; set; } = 1.0;

    public double TextureRotationDeg { get; set; }

    /// <summary>0 keeps the flat preset colour, 1 uses the texture's own colour.</summary>
    public double AlbedoStrength { get; set; } = 1.0;

    /// <summary>Amplitude of the micro-surface normal perturbation.</summary>
    public double MicroStrength { get; set; }

    /// <summary>
    /// Fraction of the texture that survives into the engraved floor. Wood grain is mostly
    /// destroyed by charring; a brushed finish is replaced entirely by a frosted one.
    /// </summary>
    public double TextureEngravedSurvival { get; set; } = 0.25;

    [JsonIgnore] public TextureMap? AlbedoTex;
    [JsonIgnore] public TextureMap? MicroTex;
    [JsonIgnore] public string? TextureError;

    private bool _resolved;

    /// <summary>Loads any referenced texture files. Safe to call every frame; caches.</summary>
    public void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        TextureError = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(AlbedoTexturePath))
                AlbedoTex = TextureMap.FromFile(AlbedoTexturePath!);
        }
        catch (Exception ex)
        {
            AlbedoTex = null;
            TextureError = "Colour texture: " + ex.Message;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(MicroTexturePath))
                MicroTex = TextureMap.FromFile(MicroTexturePath!);
        }
        catch (Exception ex)
        {
            MicroTex = null;
            TextureError = (TextureError is null ? "" : TextureError + "  ") + "Surface texture: " + ex.Message;
        }

        // Generated textures fill whatever an image file did not supply.
        if (!string.IsNullOrWhiteSpace(ProceduralTexture))
        {
            int seed = TextureMap.StableSeed(Name);
            switch (ProceduralTexture.ToLowerInvariant())
            {
                case "brushed":
                    MicroTex ??= TextureMap.Brushed(seed: seed);
                    break;

                case "wood":
                {
                    var w = TextureMap.Wood(AlbedoR, AlbedoG, AlbedoB, seed: seed);
                    AlbedoTex ??= w;
                    MicroTex ??= w;
                    break;
                }

                case "speckle":
                {
                    var s = TextureMap.Speckle(AlbedoR, AlbedoG, AlbedoB, seed: seed);
                    AlbedoTex ??= s;
                    MicroTex ??= s;
                    break;
                }
            }
        }
    }

    /// <summary>Call after changing any texture path or the procedural flag.</summary>
    public void InvalidateTextures()
    {
        _resolved = false;
        AlbedoTex = null;
        MicroTex = null;
        TextureError = null;
    }

    // ------------------------------------------------------------------ built-ins

    public static List<MaterialPreset> Builtins() => new()
    {
        new MaterialPreset
        {
            Name = "Polished brass", Metallic = true,
            AlbedoR = 0.85, AlbedoG = 0.65, AlbedoB = 0.28,
            SpecR = 1.00, SpecG = 0.66, SpecB = 0.21,
            FieldGloss = 150, EngravedGloss = 14,
            FieldSpec = 1.05, EngravedSpec = 0.42,
            EngravedTone = 0.45, EnvStrength = 0.60, Ambient = 0.10,
            TextureScale = 1.0, AlbedoStrength = 0.8, TextureEngravedSurvival = 0.12
        },
        new MaterialPreset
        {
            Name = "Brushed brass", Metallic = true,
            AlbedoR = 0.80, AlbedoG = 0.62, AlbedoB = 0.28,
            SpecR = 0.96, SpecG = 0.64, SpecB = 0.23,
            FieldGloss = 64, EngravedGloss = 10,
            FieldSpec = 0.90, EngravedSpec = 0.38,
            EngravedTone = 0.50, EnvStrength = 0.40, Ambient = 0.14,
            ProceduralTexture = "brushed", MicroStrength = 1.10,
            TextureScale = 1.0, AlbedoStrength = 0.8, TextureEngravedSurvival = 0.10
        },
        new MaterialPreset
        {
            Name = "Stainless steel", Metallic = true,
            AlbedoR = 0.76, AlbedoG = 0.77, AlbedoB = 0.79,
            SpecR = 0.92, SpecG = 0.93, SpecB = 0.95,
            FieldGloss = 120, EngravedGloss = 12,
            FieldSpec = 1.00, EngravedSpec = 0.40,
            EngravedTone = 0.55, EnvStrength = 0.55, Ambient = 0.12,
            TextureScale = 1.0, AlbedoStrength = 0.8, TextureEngravedSurvival = 0.12
        },
        new MaterialPreset
        {
            Name = "Brushed stainless", Metallic = true,
            AlbedoR = 0.74, AlbedoG = 0.75, AlbedoB = 0.77,
            SpecR = 0.91, SpecG = 0.92, SpecB = 0.94,
            FieldGloss = 58, EngravedGloss = 11,
            FieldSpec = 0.88, EngravedSpec = 0.38,
            EngravedTone = 0.58, EnvStrength = 0.38, Ambient = 0.15,
            ProceduralTexture = "brushed", MicroStrength = 1.20,
            TextureScale = 1.0, AlbedoStrength = 0.8, TextureEngravedSurvival = 0.10
        },
        new MaterialPreset
        {
            Name = "Copper", Metallic = true,
            AlbedoR = 0.90, AlbedoG = 0.55, AlbedoB = 0.42,
            SpecR = 0.96, SpecG = 0.50, SpecB = 0.33,
            FieldGloss = 130, EngravedGloss = 13,
            FieldSpec = 1.00, EngravedSpec = 0.42,
            EngravedTone = 0.48, EnvStrength = 0.55, Ambient = 0.11,
            TextureScale = 1.0, AlbedoStrength = 0.8, TextureEngravedSurvival = 0.15
        },
        new MaterialPreset
        {
            // Ablating the anodised layer exposes bright bare aluminium, so the engraving
            // comes out LIGHTER than the surface. EngravedTone above 1 handles that.
            Name = "Black anodised aluminium", Metallic = false,
            AlbedoR = 0.085, AlbedoG = 0.085, AlbedoB = 0.095,
            SpecR = 0.62, SpecG = 0.63, SpecB = 0.65,
            FieldGloss = 70, EngravedGloss = 16,
            FieldSpec = 0.55, EngravedSpec = 0.30,
            EngravedTone = 5.2, EnvStrength = 0.16, Ambient = 0.22,
            TextureScale = 1.0, AlbedoStrength = 0.7, TextureEngravedSurvival = 0.08
        },
        new MaterialPreset
        {
            Name = "Slate", Metallic = false,
            AlbedoR = 0.155, AlbedoG = 0.165, AlbedoB = 0.180,
            SpecR = 0.55, SpecG = 0.55, SpecB = 0.57,
            FieldGloss = 26, EngravedGloss = 8,
            FieldSpec = 0.30, EngravedSpec = 0.12,
            EngravedTone = 2.9, EnvStrength = 0.10, Ambient = 0.26,
            ProceduralTexture = "speckle", TextureScale = 1.0, AlbedoStrength = 1.0, MicroStrength = 0.55,
            TextureEngravedSurvival = 0.30
        },
        new MaterialPreset
        {
            Name = "Cherrywood", Metallic = false,
            AlbedoR = 0.46, AlbedoG = 0.24, AlbedoB = 0.14,
            SpecR = 0.85, SpecG = 0.82, SpecB = 0.78,
            FieldGloss = 18, EngravedGloss = 6,
            FieldSpec = 0.26, EngravedSpec = 0.09,
            EngravedTone = 0.28, EnvStrength = 0.10, Ambient = 0.30,
            ProceduralTexture = "wood", TextureScale = 1.0, AlbedoStrength = 1.0, MicroStrength = 0.55,
            TextureEngravedSurvival = 0.22
        },
        new MaterialPreset
        {
            Name = "Maple", Metallic = false,
            AlbedoR = 0.81, AlbedoG = 0.67, AlbedoB = 0.46,
            SpecR = 0.88, SpecG = 0.86, SpecB = 0.82,
            FieldGloss = 20, EngravedGloss = 6,
            FieldSpec = 0.24, EngravedSpec = 0.09,
            EngravedTone = 0.26, EnvStrength = 0.09, Ambient = 0.32,
            ProceduralTexture = "wood", TextureScale = 1.0, AlbedoStrength = 1.0, MicroStrength = 0.55,
            TextureEngravedSurvival = 0.22
        },
        new MaterialPreset
        {
            Name = "Oak", Metallic = false,
            AlbedoR = 0.63, AlbedoG = 0.47, AlbedoB = 0.29,
            SpecR = 0.86, SpecG = 0.84, SpecB = 0.80,
            FieldGloss = 16, EngravedGloss = 6,
            FieldSpec = 0.22, EngravedSpec = 0.08,
            EngravedTone = 0.30, EnvStrength = 0.09, Ambient = 0.31,
            ProceduralTexture = "wood", TextureScale = 1.0, AlbedoStrength = 1.0, MicroStrength = 0.60,
            TextureEngravedSurvival = 0.22
        },
        new MaterialPreset
        {
            Name = "Neutral plaster (shape only)", Metallic = false,
            AlbedoR = 0.78, AlbedoG = 0.78, AlbedoB = 0.78,
            SpecR = 0.6, SpecG = 0.6, SpecB = 0.6,
            FieldGloss = 14, EngravedGloss = 14,
            FieldSpec = 0.10, EngravedSpec = 0.10,
            EngravedTone = 1.0, EnvStrength = 0.06, Ambient = 0.34,
            AlbedoStrength = 0, MicroStrength = 0
        }
    };
}
