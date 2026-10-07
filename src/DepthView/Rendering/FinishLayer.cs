namespace DepthView.Rendering;

/// <summary>
/// What finishing did to the surface, per height-field sample, for the renderer to shade.
///
/// Two layers: the bare metal (its roughness, and how far a pickle has shifted it toward
/// pink copper), and over it a patina or oxide that covers a fraction of each sample. The
/// two are blended at the shading level by that coverage rather than by mixing colours,
/// which is what keeps metallic glints alive in a half-relieved recess. A clear coat, when
/// there is one, lies over everything.
///
/// Must be the same size as the scene it is drawn with; a mismatched layer is ignored.
/// </summary>
public sealed class FinishLayer
{
    public readonly int W, H;

    /// <summary>Linear F0 of the bare metal.</summary>
    public double MetalR, MetalG, MetalB;

    /// <summary>Linear colour a pickle leaves on formerly oxidised areas.</summary>
    public double PickleR, PickleG, PickleB;

    public readonly float[] MetalRough;
    public readonly float[] Pickle;

    /// <summary>Fraction of the sample covered by patina or oxide.</summary>
    public readonly float[] Cover;
    /// <summary>Linear albedo of that cover.</summary>
    public readonly float[] PatR, PatG, PatB;
    public readonly float[] PatRough;
    public readonly float[] PatF0;

    /// <summary>1 outside the blank: not part of the coin, drawn as background.</summary>
    public readonly byte[] Void;

    /// <summary>0 = no clear coat, 1 = a full one.</summary>
    public double Clear;
    public double ClearRough = 0.1;

    public FinishLayer(int w, int h)
    {
        W = w; H = h;
        long n = (long)w * h;
        MetalRough = new float[n];
        Pickle = new float[n];
        Cover = new float[n];
        PatR = new float[n];
        PatG = new float[n];
        PatB = new float[n];
        PatRough = new float[n];
        PatF0 = new float[n];
        Void = new byte[n];
    }

    /// <summary>
    /// Phong-style exponent for a perceptual roughness. Fitted so the shipped presets keep
    /// their look: about 150 for polished brass (0.1), 14 for a laser floor (0.8).
    /// </summary>
    public static double Exponent(double rough)
        => System.Math.Clamp(150.0 * System.Math.Exp(-3.39 * (rough - 0.1)), 1.5, 600);

    /// <summary>Specular strength for a roughness, on the same fit: 1.0 polished, 0.42 frosted.</summary>
    public static double SpecStrength(double rough)
    {
        double s = 1 - System.Math.Clamp(rough, 0, 1);
        return 0.35 + 0.75 * s * System.Math.Sqrt(s);
    }
}
