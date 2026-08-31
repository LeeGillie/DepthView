namespace DepthView.Processing;

/// <summary>
/// What to do to a depth map. Every field is expressed in the units of the source image, so
/// a black point of 800 means level 800 whether the container is 8-bit or 16-bit.
///
/// Convention throughout: <b>black is deepest, white is untouched.</b> That is LightBurn's
/// default for 3D Slice - darkest pixels receive every pass, pure white receives none - and
/// the same reading applies to MakeIt. <see cref="Invert"/> is there for source art that was
/// authored the other way round.
/// </summary>
public sealed class CorrectionOptions
{
    // --- levels ---------------------------------------------------------
    // The two points are a single idea: everything at or below BlackPoint becomes fully
    // deep, everything at or above WhitePoint becomes untouched, and what lies between is
    // stretched to fill the whole range. One control solves three problems at once - a
    // noisy floor that engraves mottled, a wasted range that throws away depth steps, and
    // a rim that should not be cut at all.

    /// <summary>Levels at or below this are flattened to pure black: one uniform depth.</summary>
    public int BlackPoint;

    /// <summary>Levels at or above this are lifted to pure white: no passes, untouched surface.</summary>
    public int WhitePoint;

    /// <summary>Stretch what remains between the two points across the full range.</summary>
    public bool Stretch = true;

    // --- rim ------------------------------------------------------------

    /// <summary>Paint an untouched ring at the edge, matching the raised rim on a coin blank.</summary>
    public bool AddRim;

    /// <summary>Rim centre, in pixels. Defaults to the image centre.</summary>
    public double? RimCentreX, RimCentreY;

    /// <summary>Outer radius of the engraved area, in pixels. Beyond this is pure white.</summary>
    public double RimRadius;

    /// <summary>
    /// Width of the ramp inside the rim, in pixels. The map blends toward white across this
    /// band so the engraving rises to meet the untouched rim instead of ending in a wall.
    /// </summary>
    public double RimRamp;

    // --- slicing --------------------------------------------------------

    /// <summary>Quantise to exactly this many levels, matching a pass count. 0 leaves it alone.</summary>
    public int Slices;

    /// <summary>
    /// Dither the slice boundaries. A slicer thresholds hard, so on a smooth dome the
    /// boundaries land as visible contour rings; scattering them breaks the rings up. Only
    /// meaningful together with <see cref="Slices"/>.
    /// </summary>
    public bool Dither;

    // --- output ---------------------------------------------------------

    /// <summary>Flip the sense of the map, for art authored white-deepest.</summary>
    public bool Invert;

    /// <summary>Bit depth to write. 16 unless there is a reason.</summary>
    public int OutputBitDepth = 16;

    /// <summary>Written into the PNG as a pHYs chunk, so the map imports at its true size.</summary>
    public double? Dpi;

    /// <summary>True when nothing here would change a single pixel.</summary>
    public bool IsNoOp(int maxValue) =>
        BlackPoint <= 0 && WhitePoint >= maxValue && !AddRim && Slices <= 0 && !Invert;
}
