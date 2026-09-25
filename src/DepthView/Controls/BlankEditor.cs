using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DepthView.Controls;

/// <summary>
/// Diameter, thickness and target depth of <see cref="Blank.Current"/>, editable in place.
///
/// Every window that needs these numbers hosts one of these rather than its own boxes, and all
/// of them edit the one shared <see cref="Blank"/>. Change the depth in the tuning dialog and
/// the relief window's box moves with it - there is no copy anywhere that could disagree.
/// </summary>
public sealed class BlankEditor : UserControl
{
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8A929E"));
    private static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#6B7480"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#E8B04B"));
    private static readonly IBrush StopBrush = new SolidColorBrush(Color.Parse("#E8837A"));

    private readonly NumericUpDown _diameter, _thickness, _depth;
    private readonly Button _auto;
    private readonly TextBlock _source, _warning;
    private bool _syncing;

    public BlankEditor()
    {
        _diameter = Box(Blank.MinDiameterMm, Blank.MaxDiameterMm, 1, "0.0",
            "Diameter of the blank. The short side of the image spans it, which is what turns pixels into millimetres - for the rim, the outline and for drawing depth to scale. Shared by every window.");
        _thickness = Box(Blank.MinThicknessMm, Blank.MaxThicknessMm, 0.5m, "0.0#",
            "Thickness of the stock. The target depth follows it until you type a depth of your own, and the 3D view draws the blank this thick. Shared by every window.");
        _depth = Box(Blank.MinDepthMm, Blank.MaxDepthMm, 0.05m, "0.00",
            "How deep the deepest (black) cut is meant to go. The depth map has no units - how deep it really goes is set by the laser settings - so this is your intention. Previews are drawn to it and the tuning figures are quoted against it. Typing a value stops it following the thickness. Shared by every window.");

        _auto = new Button
        {
            Content = "Auto",
            Padding = new Thickness(8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        ToolTip.SetTip(_auto, "Make the target depth follow the thickness again, at the saved percentage.");

        _source = new TextBlock { FontSize = 11, Foreground = NoteBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        _warning = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("112,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };
        AddRow(grid, 0, "Blank diameter mm", _diameter);
        AddRow(grid, 1, "Thickness mm", _thickness);
        AddRow(grid, 2, "Target depth mm", _depth);
        Grid.SetRow(_auto, 2);
        Grid.SetColumn(_auto, 2);
        grid.Children.Add(_auto);

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(_source);
        stack.Children.Add(_warning);
        Content = stack;

        _diameter.ValueChanged += (_, _) => { if (!_syncing && _diameter.Value is decimal v) Blank.Current.DiameterMm = (double)v; };
        _thickness.ValueChanged += (_, _) => { if (!_syncing && _thickness.Value is decimal v) Blank.Current.ThicknessMm = (double)v; };
        _depth.ValueChanged += (_, _) => { if (!_syncing && _depth.Value is decimal v) Blank.Current.TargetDepthMm = (double)v; };
        _auto.Click += (_, _) => Blank.Current.FollowThickness();

        Pull();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Blank.Current.Changed += OnBlankChanged;
        Pull();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The blank is static and outlives every window; a handler left on it would keep a
        // closed window alive and keep repainting it.
        Blank.Current.Changed -= OnBlankChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnBlankChanged(object? sender, EventArgs e) => Pull();

    /// <summary>Show the shared values. Guarded so showing a value never writes it back.</summary>
    private void Pull()
    {
        var b = Blank.Current;
        _syncing = true;
        try
        {
            _diameter.Value = (decimal)b.DiameterMm;
            _thickness.Value = (decimal)b.ThicknessMm;
            _depth.Value = (decimal)Math.Round(b.TargetDepthMm, 4);
        }
        finally { _syncing = false; }

        _auto.IsEnabled = !b.DepthFollowsThickness;
        _source.Text = b.DepthSourceText;

        string? warn = b.DepthWarning;
        _warning.Text = warn ?? "";
        _warning.IsVisible = warn is not null;
        _warning.Foreground = b.TargetDepthMm >= b.ThicknessMm ? StopBrush : WarnBrush;
    }

    private static NumericUpDown Box(double min, double max, decimal step, string format, string tip)
    {
        var n = new NumericUpDown
        {
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = step,
            FormatString = format,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 3)
        };
        ToolTip.SetTip(n, tip);
        return n;
    }

    private static void AddRow(Grid grid, int row, string label, Control box)
    {
        var t = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = LabelBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(t, row);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        if (row < 2) Grid.SetColumnSpan(box, 2);
        grid.Children.Add(t);
        grid.Children.Add(box);
    }
}
