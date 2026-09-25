using System;

namespace DepthView;

/// <summary>
/// The blank being worked on: diameter, thickness and the depth the deepest cut is meant to
/// reach. One instance for the whole program, so every view shows the same three numbers and
/// changing one anywhere changes it everywhere.
///
/// Target depth follows thickness - <see cref="DepthPercent"/> of it - until someone types a
/// depth, and from then on it stays where it was put. Otherwise changing the stock thickness
/// would silently overwrite a depth that was chosen on purpose. <see cref="FollowThickness"/>
/// hands control back.
///
/// Nothing here changes a pixel of any depth map. The map has no units; how deep black goes is
/// decided by the laser settings. These numbers are what the previews are drawn to, what the
/// tuning figures are quoted against, and later what a simulated depth gets compared with.
/// </summary>
public sealed class Blank
{
    public const double DefaultDiameterMm = 40.0;
    public const double DefaultThicknessMm = 4.0;
    public const double DefaultDepthPercent = 18.0;

    public const double MinDiameterMm = 1, MaxDiameterMm = 500;
    public const double MinThicknessMm = 0.1, MaxThicknessMm = 50;
    public const double MinDepthMm = 0.01, MaxDepthMm = 20;

    /// <summary>Beyond this share of the thickness, say how little floor is left.</summary>
    public const double DeepWarnFraction = 0.30;

    public static Blank Current { get; } = FromPreferences();

    private double _diameter = DefaultDiameterMm;
    private double _thickness = DefaultThicknessMm;
    private double _percent = DefaultDepthPercent;
    private double? _manualDepth;
    private bool _persist = true;
    private bool _unsaved;

    /// <summary>Raised after any of the numbers changed. Every caller is on the UI thread.</summary>
    public event EventHandler? Changed;

    public double DiameterMm
    {
        get => _diameter;
        set => Set(ref _diameter, Math.Clamp(value, MinDiameterMm, MaxDiameterMm));
    }

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
            if (diameter is > 0) DiameterMm = diameter.Value;
            if (thickness is > 0) ThicknessMm = thickness.Value;
            if (depth is > 0) TargetDepthMm = depth.Value;
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
        p.Save();
    }
}
