using System;
using System.Collections.Generic;

namespace DepthView.Integrations.Common;

/// <summary>What a layer is for, insofar as every toolchain agrees.</summary>
public enum LayerKind
{
    /// <summary>The file said something this program does not recognise. The vendor's own
    /// spelling is preserved in <see cref="CutLayer.VendorKind"/>; nothing is guessed.</summary>
    Unknown,

    /// <summary>Raster engraving of an image.</summary>
    Image,

    /// <summary>3D slice / depth engraving: the mode this whole program exists for.</summary>
    Slice3D,

    Line,
    Fill,
    FillAndLine,
    Offset,

    /// <summary>Geometry that never reaches the laser - LightBurn's T1/T2 and equivalents.</summary>
    Tool
}

/// <summary>
/// One layer's cut parameters, in units every toolchain can be converted to.
///
/// Every value is nullable, and that is the single most important thing about this type.
/// LightBurn omits any parameter sitting at its default, so a missing element means "whatever
/// this version calls normal" and not zero - and a reader that silently substitutes zero for
/// absent has invented a layer that engraves at no power. Absent stays absent all the way to
/// the point where something has to decide, and that decision is made in the open.
///
/// <see cref="Vendor"/> keeps every field verbatim as it was read, including the ones with no
/// home in this structure. Two reasons. Writing back has to preserve what it did not come to
/// change, and a field this program does not understand today is still evidence about a format
/// it is still learning.
/// </summary>
public sealed class CutLayer
{
    /// <summary>Layer number as the file states it. Image placements reference this.</summary>
    public int Index;

    public string? Name;

    public LayerKind Kind = LayerKind.Unknown;

    /// <summary>The layer type exactly as the file spelled it, before interpretation.</summary>
    public string? VendorKind;

    /// <summary>False when the layer is switched off and will not run.</summary>
    public bool? Enabled;

    /// <summary>False for tool layers and anything else the file marks as not for output.</summary>
    public bool? Output;

    /// <summary>Order the job runs in, where the format expresses that separately from index.</summary>
    public int? Priority;

    // --- power and motion, normalised -----------------------------------
    // Speed in mm/s and power as a percentage, because those are the units both LightBurn and
    // MakeIt present to the operator. A reader working in mm/min or 0..1000 converts on the way
    // in rather than leaving two conventions loose in the same structure.

    public double? SpeedMmPerSec;
    public double? MaxPowerPercent;
    public double? MinPowerPercent;

    /// <summary>Second source on a dual-tube machine, or the second power on a MOPA.</summary>
    public double? MaxPower2Percent;
    public double? MinPower2Percent;

    public int? Passes;

    /// <summary>Distance between raster lines, millimetres.</summary>
    public double? LineIntervalMm;

    /// <summary>Raster resolution where the file states DPI instead of an interval. Both are
    /// kept rather than converted: they are the same fact, but rounding one into the other and
    /// back is how a 254 dpi job becomes 253.</summary>
    public double? Dpi;

    // --- fibre and MOPA -------------------------------------------------

    public double? FrequencyKhz;

    /// <summary>MOPA pulse width, nanoseconds.</summary>
    public double? PulseWidthNs;

    public double? ZOffsetMm;
    public double? ZStepPerPassMm;

    public bool? AirAssist;

    /// <summary>Dither mode verbatim - "jarvis", "ordered", "atkinson" and friends. Left as the
    /// vendor's string because the sets do not line up between toolchains and an approximate
    /// translation would be worse than none.</summary>
    public string? DitherMode;

    // --- 3D slice -------------------------------------------------------

    /// <summary>
    /// Number of slices, where the format states it separately from the pass count.
    ///
    /// Kept apart from <see cref="Passes"/> on purpose. In LightBurn's 3D Slice the pass count
    /// is the slice count, but that is a property of that mode rather than a fact about laser
    /// jobs, and folding them together here would make a reader for some other toolchain lie
    /// to make itself fit.
    /// </summary>
    public int? SliceCount;

    /// <summary>Depth the deepest cut is intended to reach, where the file says so.</summary>
    public double? SliceDepthMm;

    /// <summary>
    /// Every field as read, keyed by the vendor's own name. Survives round-tripping and is the
    /// raw material for supporting a format that is only partly understood.
    /// </summary>
    public readonly Dictionary<string, string> Vendor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pass count to quote analysis against, or null when the file does not say.
    ///
    /// Returns the slice count in preference to the pass count, because in a 3D job that is the
    /// number that decides how many distinct depths come out. Null rather than a fallback of
    /// 256: a guessed pass count silently changes every depth figure downstream, and this
    /// program's whole argument is that those figures should come from the file.
    /// </summary>
    public int? EffectivePasses => SliceCount ?? Passes;

    public override string ToString()
        => $"[{Index}] {Name ?? "(unnamed)"} {Kind}"
         + (SpeedMmPerSec is { } s ? $" {s:0.##} mm/s" : "")
         + (MaxPowerPercent is { } p ? $" {p:0.##}%" : "")
         + (EffectivePasses is { } n ? $" x{n}" : "");
}
