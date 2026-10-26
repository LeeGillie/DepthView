using System;
using System.Collections.Generic;
using System.Globalization;

namespace DepthView;

/// <summary>
/// The outline of the workpiece. Round is the coin; Rectangle covers plates, tags and bars (a
/// corner radius makes a tag); Custom is any outline drawn as an SVG or painted as a mask,
/// sized by its bounding box.
/// </summary>
public enum BlankShape { Round, Rectangle, Custom }

/// <summary>A hole through the workpiece, in millimetres from the blank's centre (Y down), kept clear by the job like the rim.</summary>
public readonly record struct BlankHole(double XMm, double YMm, double DiameterMm);

/// <summary>
/// The blank being worked on - the workpiece: its shape and size, its thickness, and the
/// depth the deepest cut is meant to reach. One instance for the whole program, so every view
/// shows the same numbers and changing one anywhere changes it everywhere.
///
/// The coin is the round shape: a diameter, and a rim the job leaves untouched. A plate is
/// the rectangular shape: width and height, an optional corner radius (which is what makes a
/// tag), and holes to keep clear. Whatever the shape, the image spans the blank's bounding
/// box - the short side of the image is the short side of the blank, which is what turns
/// pixels into millimetres - and the reserve (rim or border, plus holes) is what the job
/// will not cut.
///
/// Target depth follows thickness - <see cref="DepthPercent"/> of it - until someone types a
/// depth, and from then on it stays where it was put. Otherwise changing the stock thickness
/// would silently overwrite a depth that was chosen on purpose. <see cref="FollowThickness"/>
/// hands control back.
///
/// Nothing here changes a pixel of any depth map. The map has no units; how deep black goes is
/// decided by the laser settings. These numbers are what the previews are drawn to, what the
/// tuning figures are quoted against, and later what a simulated depth gets compared with.
///
/// Compiled into the updater's tests without Avalonia: nothing but System may be used here.
/// </summary>
public sealed class Blank
{
    public const double DefaultDiameterMm = 40.0;
    public const double DefaultThicknessMm = 4.0;
    public const double DefaultDepthPercent = 18.0;
    public const double DefaultWidthMm = 100.0, DefaultHeightMm = 60.0;

    public const double MinDiameterMm = 1, MaxDiameterMm = 500;
    public const double MinThicknessMm = 0.1, MaxThicknessMm = 50;
    public const double MinDepthMm = 0.01, MaxDepthMm = 20;

    /// <summary>Beyond this share of the thickness, say how little floor is left.</summary>
    public const double DeepWarnFraction = 0.30;

    public static Blank Current { get; } = FromPreferences();

    private BlankShape _shape = BlankShape.Round;
    private double _diameter = DefaultDiameterMm;
    private double _width = DefaultWidthMm, _height = DefaultHeightMm, _corner;
    private List<BlankHole> _holes = new();
    private double _thickness = DefaultThicknessMm;
    private double _percent = DefaultDepthPercent;
    private double? _manualDepth;
    private bool _persist = true;
    private bool _unsaved;

    /// <summary>Raised after any of the numbers changed. Every caller is on the UI thread.</summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ shape

    public BlankShape Shape
    {
        get => _shape;
        set { if (_shape == value) return; _shape = value; Raise(); }
    }

    public bool IsRound => _shape == BlankShape.Round;

    /// <summary>
    /// The material, as an id in the material catalogue ("brass", "stainless", "wood" ...).
    /// Kept as a string here so this file stays free of everything but System; the catalogue
    /// resolves it. Nothing about the material changes a pixel: it picks the relief look and
    /// the finishing chemistry, and it is what the settings advisor and the capability gate
    /// are asked about.
    /// </summary>
    public string MaterialId
    {
        get => _material;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? "brass" : value.Trim().ToLowerInvariant();
            if (_material == v) return;
            _material = v;
            Raise();
        }
    }
    private string _material = "brass";

    /// <summary>
    /// For <see cref="BlankShape.Custom"/>: the SVG or mask file that draws the outline. The
    /// outline's bounding box is <see cref="WidthMm"/> × <see cref="HeightMm"/>; the file
    /// itself is read by the processing code (Blank stays free of everything but System).
    /// </summary>
    public string? OutlinePath
    {
        get => _outlinePath;
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_outlinePath == v) return;
            _outlinePath = v;
            Raise();
        }
    }
    private string? _outlinePath;

    /// <summary>
    /// Hook for the processing code: which pixels of a w × h map are on a custom outline.
    /// Set once at startup; null (or a null result) falls back to the bounding box.
    /// </summary>
    public static Func<Blank, int, int, Func<int, int, bool>?>? CustomOnBlank;

    /// <summary>Diameter of a round blank. For a rectangle this is its short side, so code written for coins keeps working.</summary>
    public double DiameterMm
    {
        get => IsRound ? _diameter : ShortSideMm;
        set => Set(ref _diameter, Math.Clamp(value, MinDiameterMm, MaxDiameterMm));
    }

    /// <summary>Width of a rectangular blank, mm. For a round blank, the diameter.</summary>
    public double WidthMm
    {
        get => IsRound ? _diameter : _width;
        set => Set(ref _width, Math.Clamp(value, MinDiameterMm, MaxDiameterMm));
    }

    /// <summary>Height of a rectangular blank, mm. For a round blank, the diameter.</summary>
    public double HeightMm
    {
        get => IsRound ? _diameter : _height;
        set => Set(ref _height, Math.Clamp(value, MinDiameterMm, MaxDiameterMm));
    }

    /// <summary>Corner radius of a rectangular blank, mm; 0 is a sharp corner. Ignored when round.</summary>
    public double CornerRadiusMm
    {
        get => IsRound ? 0 : Math.Min(_corner, ShortSideMm / 2);
        set => Set(ref _corner, Math.Clamp(value, 0, MaxDiameterMm / 2));
    }

    /// <summary>Holes to keep clear, in mm from the centre (Y down).</summary>
    public IReadOnlyList<BlankHole> Holes => _holes;

    public void SetHoles(IEnumerable<BlankHole> holes)
    {
        var list = new List<BlankHole>();
        foreach (var h in holes) if (h.DiameterMm > 0) list.Add(h);
        if (list.Count == _holes.Count)
        {
            bool same = true;
            for (int i = 0; i < list.Count; i++) if (!list[i].Equals(_holes[i])) { same = false; break; }
            if (same) return;
        }
        _holes = list;
        Raise();
    }

    /// <summary>The side the image's short side spans, mm.</summary>
    public double ShortSideMm => IsRound ? _diameter : Math.Min(_width, _height);
    public double LongSideMm => IsRound ? _diameter : Math.Max(_width, _height);

    /// <summary>"40 mm round" / "100 × 60 mm plate, r 5" - for titles and reports.</summary>
    public string ShapeSummary => IsRound
        ? $"{_diameter:0.#} mm round"
        : _shape == BlankShape.Custom
        ? $"{_width:0.#} × {_height:0.#} mm outline" + (_outlinePath is { } op ? $" ({System.IO.Path.GetFileName(op)})" : " (no file)") + (_holes.Count > 0 ? $", {_holes.Count} hole{(_holes.Count == 1 ? "" : "s")}" : "")
        : $"{_width:0.#} × {_height:0.#} mm {(CornerRadiusMm > 0 ? $"tag (corner r {CornerRadiusMm:0.#})" : "plate")}" + (_holes.Count > 0 ? $", {_holes.Count} hole{(_holes.Count == 1 ? "" : "s")}" : "");

    /// <summary>Holes as text, "x,y,d; x,y,d" in mm, for a box or a command line.</summary>
    public string HolesText => HolesToText(_holes);

    /// <summary>
    /// Holes that are not on the plate: their edge reaches past the plate's edge. Round
    /// blanks have no holes. For a warning in the editor, not a refusal: the reserve simply
    /// ignores the part of a hole that is off the plate.
    /// </summary>
    public IReadOnlyList<BlankHole> HolesOffPlate()
    {
        var off = new List<BlankHole>();
        if (IsRound) return off;
        foreach (var h in _holes)
            if (Math.Abs(h.XMm) + h.DiameterMm / 2 > _width / 2 + 1e-9 || Math.Abs(h.YMm) + h.DiameterMm / 2 > _height / 2 + 1e-9)
                off.Add(h);
        return off;
    }

    /// <summary>
    /// Which pixels of a w × h map are metal: the circle on the short side for a round blank,
    /// or the plate's rounded rectangle less its holes. The map is taken to span the blank's
    /// bounding box, as the tuner does.
    /// </summary>
    public Func<int, int, bool> OnBlank(int w, int h)
    {
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        if (IsRound)
        {
            double r2 = Math.Min(w, h) / 2.0; r2 *= r2;
            return (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2;
        }
        double ppmm = Math.Min(w, h) / Math.Max(1e-9, ShortSideMm);
        double corner = CornerRadiusMm * ppmm;
        double hx = w / 2.0 - corner, hy = h / 2.0 - corner;
        var holes = new List<(double X, double Y, double R2)>();
        foreach (var hole in _holes)
        {
            double r = hole.DiameterMm / 2 * ppmm;
            holes.Add((cx + hole.XMm * ppmm, cy + hole.YMm * ppmm, r * r));
        }
        // The outline itself: the custom one when its file reads, else the rounded rectangle
        // of the bounding box. Holes come off either.
        Func<int, int, bool>? custom = _shape == BlankShape.Custom ? CustomOnBlank?.Invoke(this, w, h) : null;
        Func<int, int, bool> outline = custom ?? ((x, y) =>
        {
            double qx = Math.Abs(x - cx) - hx, qy = Math.Abs(y - cy) - hy;
            return !(qx > 0 && qy > 0 && qx * qx + qy * qy > corner * corner);
        });
        if (holes.Count == 0) return outline;
        return (x, y) =>
        {
            if (!outline(x, y)) return false;
            foreach (var hole in holes)
                if ((x - hole.X) * (x - hole.X) + (y - hole.Y) * (y - hole.Y) <= hole.R2) return false;
            return true;
        };
    }

    public static string HolesToText(IReadOnlyList<BlankHole> holes)
    {
        var c = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        foreach (var h in holes) parts.Add($"{h.XMm.ToString("0.##", c)},{h.YMm.ToString("0.##", c)},{h.DiameterMm.ToString("0.##", c)}");
        return string.Join("; ", parts);
    }

    /// <summary>Parses "x,y,d; x,y,d". Entries that do not parse are skipped; null on no input.</summary>
    public static List<BlankHole> ParseHoles(string? text)
    {
        var list = new List<BlankHole>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var c = CultureInfo.InvariantCulture;
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (f.Length != 3) continue;
            if (double.TryParse(f[0], NumberStyles.Float, c, out double x) && double.TryParse(f[1], NumberStyles.Float, c, out double y)
                && double.TryParse(f[2], NumberStyles.Float, c, out double d) && d > 0)
                list.Add(new BlankHole(x, y, d));
        }
        return list;
    }

    // ------------------------------------------------------------------ thickness and depth

    public double ThicknessMm
    {
        get => _thickness;
        set => Set(ref _thickness, Math.Clamp(value, MinThicknessMm, MaxThicknessMm));
    }

    public double DepthPercent
    {
        get => _percent;
        set => Set(ref _percent, Math.Clamp(value, 1, 100));
    }

    /// <summary>True while the target depth is derived from thickness rather than typed.</summary>
    public bool DepthFollowsThickness => _manualDepth is null;

    /// <summary>
    /// How deep the deepest (black) cut is meant to go. Setting it pins it: from then on it no
    /// longer follows thickness until <see cref="FollowThickness"/> is called.
    /// </summary>
    public double TargetDepthMm
    {
        get => _manualDepth ?? Math.Clamp(_thickness * _percent / 100.0, MinDepthMm, MaxDepthMm);
        set
        {
            double v = Math.Clamp(value, MinDepthMm, MaxDepthMm);
            if (_manualDepth is double m && Math.Abs(m - v) < 1e-9) return;

            // Typing back exactly the derived figure is not a decision to pin it.
            if (_manualDepth is null && Math.Abs(TargetDepthMm - v) < 1e-9) return;

            _manualDepth = v;
            Raise();
        }
    }

    public void FollowThickness()
    {
        if (_manualDepth is null) return;
        _manualDepth = null;
        Raise();
    }

    /// <summary>Material left under the deepest cut.</summary>
    public double FloorMm => _thickness - TargetDepthMm;

    public double DepthFraction => TargetDepthMm / _thickness;

    /// <summary>One line on where the depth came from, for under the depth box.</summary>
    public string DepthSourceText => DepthFollowsThickness
        ? $"Auto: {_percent:0.#}% of the {_thickness:0.0#} mm thickness."
        : $"Set by hand ({DepthFraction * 100:0}% of the thickness).";

    /// <summary>
    /// Null when the depth is unremarkable. Not a limit: deep relief on thick stock is a
    /// legitimate choice, it just deserves to be made knowingly.
    /// </summary>
    public string? DepthWarning
    {
        get
        {
            double d = TargetDepthMm;
            if (d >= _thickness)
                return $"Deeper than the blank is thick ({d:0.00} mm into {_thickness:0.0#} mm). The cut would go right through.";
            if (DepthFraction > DeepWarnFraction)
                return $"{DepthFraction * 100:0}% of the thickness - only {FloorMm:0.00} mm left under the deepest cut.";
            return null;
        }
    }

    /// <summary>
    /// Apply command-line values without writing them to the saved preferences, so a scripted
    /// run with --blank 25 does not become the next session's default.
    /// </summary>
    public void ApplyTransient(double? diameter, double? thickness, double? depth)
    {
        _persist = false;
        try
        {
            if (diameter is > 0) { Shape = BlankShape.Round; DiameterMm = diameter.Value; }
            if (thickness is > 0) ThicknessMm = thickness.Value;
            if (depth is > 0) TargetDepthMm = depth.Value;
        }
        finally { _persist = true; }
    }

    /// <summary>A material from the command line (--material), not saved to the preferences.</summary>
    public void ApplyTransientMaterial(string id)
    {
        _persist = false;
        try { MaterialId = id; }
        finally { _persist = true; }
    }

    /// <summary>A rectangular blank from the command line (--plate), not saved to the preferences.</summary>
    public void ApplyTransientPlate(double width, double height, double corner, IEnumerable<BlankHole>? holes)
    {
        _persist = false;
        try
        {
            Shape = BlankShape.Rectangle;
            WidthMm = width; HeightMm = height; CornerRadiusMm = corner;
            if (holes is not null) SetHoles(holes);
        }
        finally { _persist = true; }
    }

    /// <summary>A custom outline from the command line (--shape), with the bounding box when the file does not state one; not saved.</summary>
    public void ApplyTransientOutline(string path, double? width, double? height)
    {
        _persist = false;
        try
        {
            Shape = BlankShape.Custom;
            OutlinePath = path;
            if (width is > 0) WidthMm = width.Value;
            if (height is > 0) HeightMm = height.Value;
        }
        finally { _persist = true; }
    }

    private void Set(ref double field, double value)
    {
        if (Math.Abs(field - value) < 1e-9) return;
        field = value;
        Raise();
    }

    private void Raise()
    {
        if (_persist) _unsaved = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Blank FromPreferences()
    {
        var p = Preferences.Current;
        var b = new Blank { _persist = false };
        b.DiameterMm = p.BlankDiameterMm;
        b.ThicknessMm = p.BlankThicknessMm;
        b.DepthPercent = p.DepthPercent;
        if (p.TargetDepthMm is > 0) b._manualDepth = Math.Clamp(p.TargetDepthMm.Value, MinDepthMm, MaxDepthMm);
        if (p.BlankWidthMm > 0) b.WidthMm = p.BlankWidthMm;
        if (p.BlankHeightMm > 0) b.HeightMm = p.BlankHeightMm;
        b.CornerRadiusMm = p.BlankCornerRadiusMm;
        b._holes = ParseHoles(p.BlankHoles);
        b._shape = string.Equals(p.BlankShape, "rectangle", StringComparison.OrdinalIgnoreCase) ? BlankShape.Rectangle
                 : string.Equals(p.BlankShape, "custom", StringComparison.OrdinalIgnoreCase) ? BlankShape.Custom
                 : BlankShape.Round;
        b._outlinePath = string.IsNullOrWhiteSpace(p.BlankOutlinePath) ? null : p.BlankOutlinePath.Trim();
        if (!string.IsNullOrWhiteSpace(p.BlankMaterial)) b._material = p.BlankMaterial.Trim().ToLowerInvariant();
        b._persist = true;
        return b;
    }

    /// <summary>
    /// Write the blank to the preferences if someone changed it. Called when a window that
    /// edits it closes, the same as the pass count: spinning a box through 3, 3.5, 4 should not
    /// write the file three times. Values that only came from the command line are not saved.
    /// </summary>
    public void SaveIfChanged()
    {
        if (!_unsaved) return;
        _unsaved = false;

        var p = Preferences.Current;
        p.BlankDiameterMm = _diameter;
        p.BlankThicknessMm = _thickness;
        p.DepthPercent = _percent;
        p.TargetDepthMm = _manualDepth;
        p.BlankShape = _shape switch { BlankShape.Rectangle => "rectangle", BlankShape.Custom => "custom", _ => "round" };
        p.BlankOutlinePath = _outlinePath;
        p.BlankWidthMm = _width;
        p.BlankHeightMm = _height;
        p.BlankCornerRadiusMm = _corner;
        p.BlankHoles = HolesToText(_holes);
        p.BlankMaterial = _material;
        p.Save();
    }
}
