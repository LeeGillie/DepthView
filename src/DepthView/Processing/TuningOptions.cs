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
public sealed class TuningOptions
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

    // --- physical size ---------------------------------------------------
    // Rim geometry is measured with calipers, not guessed as a percentage. Given the blank
    // diameter, every other dimension can be stated in millimetres and converted once, and
    // the resulting resolution can be written into the file so an importer places it at the
    // right size without anyone scaling it by hand - which is one of the easier ways to
    // ruin a coin.

    /// <summary>Diameter of the blank, in mm, matched to the short side of the image.</summary>
    public double? BlankDiameterMm;

    /// <summary>Rim width in mm, measured from the edge inward.</summary>
    public double? RimWidthMm;

    /// <summary>
    /// Ramp width in mm. Zero is valid and means a hard step at the rim.
    ///
    /// Worth understanding before setting it. The map cannot express an edge sharper than one
    /// pixel, and the beam smears any transition to roughly its own spot size whatever the map
    /// says - so a ramp between zero and about one spot diameter achieves nothing the beam was
    /// not going to do anyway. Either ask for a hard step and let the optics decide, or make
    /// the ramp wide enough to be a deliberate shoulder.
    ///
    /// The map does not control the wall angle either. It states a depth per pixel; what comes
    /// out is whatever ablation produces, and ablated pockets taper as they deepen. Whether a
    /// given machine holds a near-vertical wall at a given depth is a question for a test
    /// piece, not for a default in a config file.
    /// </summary>
    public double? RimRampMm;

    /// <summary>
    /// Intended full engraving depth in mm. Never changes a pixel - it is only used to report
    /// the geometry the settings imply: microns per pass, and the wall angle the ramp asks for.
    /// </summary>
    public double? TargetDepthMm;

    /// <summary>Pixels per mm implied by the blank diameter and the image's short side.</summary>
    public double? PixelsPerMm(int width, int height) =>
        BlankDiameterMm is > 0 ? Math.Min(width, height) / BlankDiameterMm : null;

    /// <summary>
    /// Turns the millimetre figures into pixels and fills in the DPI. Call once, before
    /// <see cref="DepthTuner.Apply"/>, when the physical size is known.
    /// </summary>
    public void ResolvePhysical(int width, int height)
    {
        if (PixelsPerMm(width, height) is not double ppmm || ppmm <= 0) return;

        double half = Math.Min(width, height) / 2.0;
        if (RimWidthMm is > 0)
        {
            AddRim = true;
            RimRadius = Math.Max(1, half - RimWidthMm.Value * ppmm);
            // A ramp is not assumed. Unstated means none: a hard step is what a coin blank's
            // own rim looks like, and a shoulder should be asked for rather than arrive by
            // default in a file someone is about to cut metal from.
            RimRamp = (RimRampMm ?? 0) * ppmm;
        }
        Dpi ??= ppmm * 25.4;
    }

    /// <summary>True when nothing here would change a single pixel.</summary>
    public bool IsNoOp(int maxValue) =>
        BlackPoint <= 0 && WhitePoint >= maxValue && !AddRim && Slices <= 0 && !Invert;
}

/// <summary>How the file's resolution compares with what the machine can actually resolve.</summary>
public readonly record struct ResolutionCheck(double MicronsPerPixel, string Note)
{
    /// <summary>
    /// A depth map finer than the beam is wasted work, and one coarser than the beam throws
    /// away detail the machine could have cut. Both are worth knowing before a long job.
    /// </summary>
    public static ResolutionCheck For(double micronsPerPixel, double spotMicrons)
    {
        double ratio = micronsPerPixel / spotMicrons;
        string note = ratio switch
        {
            < 0.5 => "finer than the beam can resolve - the extra pixels cost time and buy nothing",
            < 0.9 => "comfortably finer than the spot",
            <= 1.6 => "well matched to the spot",
            <= 3.0 => "coarser than the spot - a higher resolution map would carry more detail",
            _ => "much coarser than the spot - the machine can cut finer than this file describes",
        };
        return new ResolutionCheck(micronsPerPixel, note);
    }
}
