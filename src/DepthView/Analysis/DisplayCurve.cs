using System;
using DepthView.Imaging;

namespace DepthView.Analysis;

public enum CurveKind { None, Gamma, Srgb, IccOnly }

/// <summary>
/// "As stored" against "as if linear" (TODO 9.6). A PNG can declare that its values went
/// through a display curve - an sRGB chunk, a gAMA other than 1, an ICC profile. Depth is
/// meant to be linear: equal steps in value, equal steps in depth. If a map really was
/// exported through a display transform (a renderer's default sRGB view instead of Raw, say),
/// its depth is bent - one end of the range squeezed, the other stretched.
///
/// The tag alone does not prove it: many exporters stamp sRGB or gamma 1/2.2 on every PNG
/// whatever they did to the values. And a curve the file does not declare cannot be found
/// from the pixels. So DepthView says what the declared curve would mean, offers a view as if
/// it were undone, and goes on using the values as stored, as a laser program does.
/// </summary>
public sealed class DisplayCurve
{
    public CurveKind Kind { get; }

    /// <summary>The gAMA value as stored: 0.45455 for the usual 1/2.2 encoding.</summary>
    public double FileGamma { get; }

    public string? ProfileName { get; }

    private DisplayCurve(CurveKind kind, double fileGamma, string? profile)
    {
        Kind = kind;
        FileGamma = fileGamma;
        ProfileName = profile;
    }

    public static DisplayCurve Of(ImageMetadata m)
    {
        // The PNG specification has sRGB override gAMA when both are present.
        if (m.SrgbIntent is not null) return new DisplayCurve(CurveKind.Srgb, 1 / 2.2, m.IccProfileName);
        if (m.Gamma is double g && g > 0 && Math.Abs(g - 1.0) >= 0.001)
            return new DisplayCurve(CurveKind.Gamma, g, m.IccProfileName);
        if (m.HasIccProfile) return new DisplayCurve(CurveKind.IccOnly, 1, m.IccProfileName);
        return new DisplayCurve(CurveKind.None, 1, null);
    }

    /// <summary>True when the curve is known well enough to undo: sRGB, or a plain gamma.</summary>
    public bool CanUndo => Kind is CurveKind.Gamma or CurveKind.Srgb;

    /// <summary>A stored value, 0..1, as the linear value it stands for if the curve was applied.</summary>
    public double ToLinear(double v)
    {
        v = Math.Clamp(v, 0, 1);
        return Kind switch
        {
            CurveKind.Srgb => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4),
            CurveKind.Gamma => Math.Pow(v, 1.0 / FileGamma),
            _ => v,
        };
    }

    /// <summary>
    /// How far down, as a share of the full depth, a level stored halfway down would be once
    /// the curve is undone. 0.5 means no bend; about 0.79 for sRGB.
    /// </summary>
    public double DepthAtStoredHalf => 1 - ToLinear(0.5);

    public string Name => Kind switch
    {
        CurveKind.Srgb => "sRGB",
        CurveKind.Gamma => $"gamma {1 / FileGamma:0.##} (gAMA {FileGamma:0.#####})",
        CurveKind.IccOnly => ProfileName is { Length: > 0 } p ? $"an ICC profile ({p})" : "an ICC profile",
        _ => "none",
    };

    /// <summary>JSON's name for the kind: srgb, gamma, icc, or null.</summary>
    public string? JsonKind => Kind switch
    {
        CurveKind.Srgb => "srgb",
        CurveKind.Gamma => "gamma",
        CurveKind.IccOnly => "icc",
        _ => null,
    };
}
