using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Rendering;

namespace DepthView.Controls;

/// <summary>
/// Colours an exaggeration slider, and the text around it, by how far the view is from true
/// scale: green at 1:1, through yellow, to red. The colour moves continuously with the slider,
/// so nobody has to read a number to know whether they are looking at the real thing or at a
/// magnified one.
///
/// One mutable brush is installed under every key the Fluent slider template looks up for its
/// filled track and thumb, so a slider move only changes a colour - no restyle, no re-template.
/// </summary>
public sealed class ZScaleHint
{
    private static readonly string[] SliderKeys =
    {
        "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
        "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed"
    };

    private readonly SolidColorBrush _brush = new(Colors.LimeGreen);
    private readonly Slider _slider;
    private readonly TextBlock[] _text;
    private bool _snapping;

    public IBrush Brush => _brush;

    public ZScaleHint(Slider slider, params TextBlock[] text)
    {
        _slider = slider;
        _text = text;

        _slider.Minimum = ZScale.MinStops;
        _slider.Maximum = ZScale.MaxStops;
        foreach (var k in SliderKeys) _slider.Resources[k] = _brush;
        _slider.Foreground = _brush;
    }

    /// <summary>
    /// Pull a value that is almost zero onto zero. A continuous slider otherwise can never quite
    /// land on true scale, and "0.03 stops" would show as not-to-scale for no visible reason.
    /// Returns true when it moved the slider, in which case the caller's change handler will run
    /// again with the snapped value and this pass should do nothing further.
    /// </summary>
    public bool Snap()
    {
        if (_snapping) return false;
        double v = _slider.Value;
        if (v == 0 || Math.Abs(v) >= ZScale.Detent) return false;

        _snapping = true;
        try { _slider.Value = 0; }
        finally { _snapping = false; }
        return true;
    }

    public double Stops => _slider.Value;

    public void ToTrueScale() => _slider.Value = 0;

    /// <summary>Recolour everything for the slider's current value.</summary>
    public void Paint()
    {
        var (r, g, b) = ZScale.HintRgb(_slider.Value);
        _brush.Color = Color.FromRgb(r, g, b);
        foreach (var t in _text) t.Foreground = _brush;
    }
}

/// <summary>
/// A small label that sits on the rendered surface and says how Z is scaled. On the picture
/// rather than beside it, because a screenshot of the picture is what travels.
/// </summary>
public sealed class ZScaleBadge : Border
{
    private readonly TextBlock _text = new()
    {
        FontSize = 11,
        FontWeight = FontWeight.SemiBold
    };

    private readonly SolidColorBrush _brush = new(Colors.LimeGreen);

    public ZScaleBadge()
    {
        Child = _text;
        Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x12, 0x15));
        BorderBrush = _brush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(7, 3);
        Margin = new Thickness(8);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;
        _text.Foreground = _brush;
        Update(0);
    }

    public void Update(double stops)
    {
        var (r, g, b) = ZScale.HintRgb(stops);
        _brush.Color = Color.FromRgb(r, g, b);
        _text.Text = ZScale.BadgeText(stops);
    }
}
