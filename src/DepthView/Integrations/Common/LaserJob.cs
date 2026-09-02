using System;
using System.Collections.Generic;
using System.Linq;

namespace DepthView.Integrations.Common;

/// <summary>
/// An image sitting in a project, and the layer that will engrave it.
///
/// The bytes are carried rather than the decoded pixels. A project can hold several megabytes
/// of embedded PNG per placement, and most questions asked of a project - which layer, what
/// size, what pass count - never need the image decoded at all. Decoding is the caller's
/// decision, made when it has a reason.
/// </summary>
public sealed class ImagePlacement
{
    /// <summary>Which <see cref="CutLayer.Index"/> engraves this image.</summary>
    public int LayerIndex;

    /// <summary>Path the project recorded, which may be stale, relative, UNC, or from another
    /// machine entirely. Present for provenance, never assumed to resolve.</summary>
    public string? SourcePath;

    /// <summary>The embedded image exactly as stored, still encoded. Null when the project
    /// references a file without embedding it.</summary>
    public byte[]? Data;

    /// <summary>Placed size on the workpiece, millimetres, once the transform is applied.</summary>
    public double? WidthMm, HeightMm;

    /// <summary>Centre of the placement on the bed, millimetres.</summary>
    public double? CentreXMm, CentreYMm;

    /// <summary>Pixel dimensions of the source, where the project states them separately from
    /// the placed size.</summary>
    public int? PixelWidth, PixelHeight;

    /// <summary>Affine transform as the file stated it, unmodified: a b c d e f, mapping source
    /// units to bed millimetres. Kept raw so a writer can put back exactly what it found.</summary>
    public double[]? Transform;

    /// <summary>
    /// True when <see cref="Data"/> holds the image with its rows bottom-up, so a decoder has
    /// to flip it to get the orientation the artwork was authored in.
    ///
    /// A property of how the format stores pixels, not of how this particular piece sits on the
    /// bed - the transform covers that separately and can rotate or mirror on top. Kept as a
    /// flag rather than applied in the reader because the reader deals in the file's own bytes
    /// and re-encoding them to correct an orientation would throw away that exactness for a
    /// presentation detail.
    /// </summary>
    public bool StoredBottomUp;

    public readonly Dictionary<string, string> Vendor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Millimetres per source pixel, when both the placed size and the pixel count are known.
    /// This is the number that decides whether a map has more resolution than the spot can use,
    /// and it cannot be worked out from the image alone - which is the whole reason for reading
    /// the project rather than just the PNG.
    /// </summary>
    public double? MmPerPixel =>
        WidthMm is { } w && PixelWidth is { } px && px > 0 ? w / px : null;
}

/// <summary>
/// A laser project as this program needs to understand it, whoever wrote it.
///
/// Deliberately not a faithful model of any one format. It carries the things an analysis has
/// to know - which images are in the job, how big they are on the workpiece, and the cut
/// parameters of the layer that will engrave each one - and nothing else. Everything else stays
/// in <see cref="Source"/>, untouched, so that writing a project back changes the fields that
/// were asked for and leaves the rest byte-identical.
///
/// That last point is a promise to the user rather than an implementation detail. Somebody's
/// project file is hours of their work, and a tool that rewrites it wholesale to change one
/// pass count is a tool that will eventually eat something irreplaceable.
/// </summary>
public sealed class LaserJob
{
    /// <summary>Which reader produced this, for messages and for choosing a writer.</summary>
    public string Format = "unknown";

    /// <summary>Path it was read from, if any.</summary>
    public string? Path;

    /// <summary>Application and version string the file declares, verbatim.</summary>
    public string? AppVersion;

    /// <summary>Machine the project was authored for, as the file names it.</summary>
    public string? DeviceName;

    public readonly List<CutLayer> Layers = new();
    public readonly List<ImagePlacement> Images = new();

    /// <summary>
    /// The document as read, for readers that can write back in place. Type is deliberately
    /// open - an XDocument for LightBurn, whatever WeCreat turns out to need - because forcing
    /// a common representation on containers that share nothing would buy nothing.
    /// </summary>
    public object? Source;

    /// <summary>
    /// Anything the reader understood but could not fit, and anything it wants a human to see.
    /// A format that is only partly documented will produce entries here, and that is the
    /// honest outcome rather than a silent partial read.
    /// </summary>
    public readonly List<string> Notes = new();

    public CutLayer? LayerFor(ImagePlacement image)
        => Layers.FirstOrDefault(l => l.Index == image.LayerIndex);

    /// <summary>
    /// The image this program is most likely to have been opened for, or null.
    ///
    /// Prefers a placement on a 3D slice layer, then the largest embedded image. A project with
    /// one image has an obvious answer; a project with several does not, and the caller is
    /// expected to ask rather than let this choose for it.
    /// </summary>
    public ImagePlacement? PrimaryImage
    {
        get
        {
            var sliced = Images.FirstOrDefault(i => LayerFor(i)?.Kind == LayerKind.Slice3D);
            if (sliced is not null) return sliced;

            return Images
                .OrderByDescending(i => i.Data?.Length ?? 0)
                .FirstOrDefault();
        }
    }
}
