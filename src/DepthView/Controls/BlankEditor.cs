using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

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

    private readonly NumericUpDown _diameter, _thickness, _depth, _width, _height, _corner;
    private readonly ComboBox _shape, _material;
    private readonly TextBox _holes, _outline;
    private readonly Button _auto, _browse;
    private readonly TextBlock _source, _warning;
    private readonly Grid _roundRows, _rectRows, _cornerRow, _outlineRow, _holesRow;
    private bool _syncing;

    public BlankEditor()
    {
        _shape = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3) };
        _shape.Items.Add(new ComboBoxItem { Content = "Round blank (coin)" });
        _shape.Items.Add(new ComboBoxItem { Content = "Rectangular plate or tag" });
        _shape.Items.Add(new ComboBoxItem { Content = "Custom outline (SVG or mask)" });
        ToolTip.SetTip(_shape, "The outline of the workpiece. A round blank has a diameter and a rim; a plate has a width and height, optional rounded corners (a tag) and holes to keep clear; a custom outline is any shape drawn as an SVG or painted as a mask image (white = the blank), sized by its bounding box. The image spans the blank's bounding box whatever the shape. Shared by every window.");
        _outline = new TextBox { FontSize = 12, Watermark = "outline.svg or mask.png", Margin = new Thickness(0, 3, 0, 3) };
        ToolTip.SetTip(_outline, "The file that draws the outline: an SVG (paths, rects, circles, ellipses, polygons; even-odd fill, so an inner contour is a hole) or an image where white is the blank. An SVG with millimetre units sets the width and height below; otherwise type them.");
        _browse = new Button { Content = "...", Padding = new Thickness(8, 3), Margin = new Thickness(6, 3, 0, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_browse, "Choose the outline file.");
        _diameter = Box(Blank.MinDiameterMm, Blank.MaxDiameterMm, 1, "0.0",
            "Diameter of the blank. The short side of the image spans it, which is what turns pixels into millimetres - for the rim, the outline and for drawing depth to scale. Shared by every window.");
        _width = Box(Blank.MinDiameterMm, Blank.MaxDiameterMm, 1, "0.0", "Width of the plate, mm. The image's width spans it.");
        _height = Box(Blank.MinDiameterMm, Blank.MaxDiameterMm, 1, "0.0", "Height of the plate, mm. The image's height spans it; the image's short side is what sets pixels per millimetre.");
        _corner = Box(0, Blank.MaxDiameterMm / 2, 0.5m, "0.0", "Corner radius, mm. 0 is a sharp corner; a few millimetres makes a tag. The corners outside the radius are left untouched like the rim.");
        _holes = new TextBox { FontSize = 12, Watermark = "x,y,diameter; x,y,diameter  (mm from the centre)", Margin = new Thickness(0, 3, 0, 3) };
        ToolTip.SetTip(_holes, "Holes to keep clear, as x,y,diameter in millimetres from the plate's centre (y down), separated by semicolons - e.g. 0,-22,4 for a hanging hole 22 mm above centre. The border width keeps the same distance clear around each hole.");
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

        _material = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var m in Materials.MaterialCatalog.All)
            _material.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
        ToolTip.SetTip(_material, "What the blank is made of. Changes no pixel: it picks the relief look and the finishing chemistry, it is what the settings advisor is asked about, and it is checked against the laser in the JOB section - a source that cannot engrave this material is refused with the reason. Shared by every window.");

        var shapeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        AddRow(shapeGrid, 0, "Shape", _shape, span: false);
        AddRow(shapeGrid, 1, "Material", _material, span: false);

        _roundRows = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), RowDefinitions = new RowDefinitions("Auto") };
        AddRow(_roundRows, 0, "Blank diameter mm", _diameter, span: false);

        // The outline file row sits above the sizes: for a custom shape the file comes first
        // and may set them. The corner radius is a plate's alone.
        _outlineRow = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*,Auto"), RowDefinitions = new RowDefinitions("Auto") };
        AddRow(_outlineRow, 0, "Outline file", _outline, span: false);
        Grid.SetRow(_browse, 0); Grid.SetColumn(_browse, 2);
        _outlineRow.Children.Add(_browse);

        _rectRows = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        AddRow(_rectRows, 0, "Width mm", _width, span: false);
        AddRow(_rectRows, 1, "Height mm", _height, span: false);
        _cornerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), RowDefinitions = new RowDefinitions("Auto") };
        AddRow(_cornerRow, 0, "Corner radius mm", _corner, span: false);
        _holesRow = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*"), RowDefinitions = new RowDefinitions("Auto") };
        AddRow(_holesRow, 0, "Holes", _holes, span: false);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("112,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto")
        };
        AddRow(grid, 0, "Thickness mm", _thickness);
        AddRow(grid, 1, "Target depth mm", _depth, span: false);
        Grid.SetRow(_auto, 1);
        Grid.SetColumn(_auto, 2);
        grid.Children.Add(_auto);

        var stack = new StackPanel();
        stack.Children.Add(shapeGrid);
        stack.Children.Add(_roundRows);
        stack.Children.Add(_outlineRow);
        stack.Children.Add(_rectRows);
        stack.Children.Add(_cornerRow);
        stack.Children.Add(_holesRow);
        stack.Children.Add(grid);
        stack.Children.Add(_source);
        stack.Children.Add(_warning);
        Content = stack;

        _shape.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            Blank.Current.Shape = _shape.SelectedIndex switch { 1 => BlankShape.Rectangle, 2 => BlankShape.Custom, _ => BlankShape.Round };
        };
        _outline.LostFocus += (_, _) => { if (!_syncing) UseOutlineFile(_outline.Text); };
        _browse.Click += async (_, _) => await BrowseOutlineAsync();
        _material.SelectionChanged += (_, _) => { if (!_syncing && (_material.SelectedItem as ComboBoxItem)?.Tag is string id) Blank.Current.MaterialId = id; };
        _width.ValueChanged += (_, _) => { if (!_syncing && _width.Value is decimal v) Blank.Current.WidthMm = (double)v; };
        _height.ValueChanged += (_, _) => { if (!_syncing && _height.Value is decimal v) Blank.Current.HeightMm = (double)v; };
        _corner.ValueChanged += (_, _) => { if (!_syncing && _corner.Value is decimal v) Blank.Current.CornerRadiusMm = (double)v; };
        _holes.LostFocus += (_, _) => { if (!_syncing) Blank.Current.SetHoles(Blank.ParseHoles(_holes.Text)); };
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
            bool custom = b.Shape == BlankShape.Custom;
            _shape.SelectedIndex = b.IsRound ? 0 : custom ? 2 : 1;
            int mi = 0;
            for (int k = 0; k < _material.Items.Count; k++)
                if ((_material.Items[k] as ComboBoxItem)?.Tag is string id && id == Materials.MaterialCatalog.Find(b.MaterialId).Id) { mi = k; break; }
            _material.SelectedIndex = mi;
            _roundRows.IsVisible = b.IsRound;
            _outlineRow.IsVisible = custom;
            _rectRows.IsVisible = !b.IsRound;
            _cornerRow.IsVisible = b.Shape == BlankShape.Rectangle;
            _holesRow.IsVisible = !b.IsRound;
            if (b.IsRound) _diameter.Value = (decimal)b.DiameterMm;
            else
            {
                _width.Value = (decimal)b.WidthMm;
                _height.Value = (decimal)b.HeightMm;
                _corner.Value = (decimal)b.CornerRadiusMm;
                if (!_holes.IsFocused) _holes.Text = b.HolesText;
                if (!_outline.IsFocused) _outline.Text = b.OutlinePath ?? "";
            }
            _thickness.Value = (decimal)b.ThicknessMm;
            _depth.Value = (decimal)Math.Round(b.TargetDepthMm, 4);
        }
        finally { _syncing = false; }

        _auto.IsEnabled = !b.DepthFollowsThickness;
        _source.Text = b.DepthSourceText;

        string? warn = b.DepthWarning;
        // A hole that reaches past the plate's edge is almost always a typo (y up instead of
        // down, or a radius typed as a diameter); said here, where it was typed.
        var off = b.HolesOffPlate();
        if (off.Count > 0)
        {
            string holeWarn = (off.Count == 1 ? "Hole " : "Holes ") + Blank.HolesToText(off)
                + (off.Count == 1 ? " reaches" : " reach") + $" past the edge of the {b.WidthMm:0.#} × {b.HeightMm:0.#} mm plate"
                + " (x, y are from the centre, y down).";
            warn = warn is null ? holeWarn : holeWarn + " " + warn;
        }
        // A custom outline whose file does not read is a plate with square corners until it
        // does; say so beside the box rather than drawing the stand-in silently.
        if (b.Shape == BlankShape.Custom)
        {
            var outline = Processing.CustomOutline.Load(b.OutlinePath, out string? outlineError);
            string? outlineWarn = outline is null ? (outlineError ?? "No outline file.") + " The bounding box stands in as a plate."
                                : outline.Problems.Count > 0 ? string.Join(" ", outline.Problems) : null;
            if (outlineWarn is not null) warn = warn is null ? outlineWarn : outlineWarn + " " + warn;
        }
        _warning.Text = warn ?? "";
        _warning.IsVisible = warn is not null;
        _warning.Foreground = b.TargetDepthMm >= b.ThicknessMm ? StopBrush : WarnBrush;
    }

    /// <summary>A typed or chosen outline file: load it, take its size when it states one, and make the blank custom.</summary>
    private void UseOutlineFile(string? path)
    {
        var b = Blank.Current;
        if (string.IsNullOrWhiteSpace(path)) { b.OutlinePath = null; return; }
        var outline = Processing.CustomOutline.Load(path, out _);
        b.OutlinePath = outline?.Path ?? path.Trim();
        if (outline is null) return;
        if (b.Shape != BlankShape.Custom) b.Shape = BlankShape.Custom;
        if (outline.WidthMm is { } wmm && outline.HeightMm is { } hmm && wmm > 0 && hmm > 0) { b.WidthMm = wmm; b.HeightMm = hmm; }
        else b.HeightMm = b.WidthMm / outline.Aspect;   // keep the width, take the file's proportions
    }

    private async System.Threading.Tasks.Task BrowseOutlineAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose the outline: an SVG, or an image where white is the blank",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Outlines (SVG, PNG, BMP, TIFF)") { Patterns = new[] { "*.svg", "*.png", "*.bmp", "*.tif", "*.tiff", "*.pgm" } },
                new Avalonia.Platform.Storage.FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        var file = files.Count > 0 ? files[0] : null;
        if (file?.TryGetLocalPath() is string localPath) UseOutlineFile(localPath);
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

    private static void AddRow(Grid grid, int row, string label, Control box, bool span = true)
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
        if (span && grid.ColumnDefinitions.Count > 2) Grid.SetColumnSpan(box, 2);
        grid.Children.Add(t);
        grid.Children.Add(box);
    }
}
