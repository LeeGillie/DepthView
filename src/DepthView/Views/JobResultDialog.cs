using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Ablation;

namespace DepthView.Views;

/// <summary>
/// After a real job: the settings it ran with and the depth it reached, entered by hand and
/// kept as grade-A evidence in the "Jobs - material on laser" study. This is how a person who
/// engraves rather than cuts coupons still teaches the advisor, and how the advice is corrected
/// after the settings were used. The settings start as the sheet suggested and are edited to
/// what was actually typed into the program.
/// </summary>
public sealed class JobResultDialog : Window
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Amber = B("#F0C674"), Green = B("#5CC28A");

    private readonly AblationDatabase _db;
    private readonly MaterialSpec _material;
    private readonly LaserSpec _laser;
    private readonly NumericUpDown _power = Num(0, 100, 1, "0.#"), _speed = Num(1, 20000, 50, "0"), _density = Num(1, 2000, 10, "0"),
        _freq = Num(1, 4000, 5, "0.#"), _pulse = Num(1, 1000, 10, "0"), _layers = Num(1, 65535, 16, "0"), _focus = Num(-20, 20, 0.1, "0.0#"), _hatch = Num(0, 180, 1, "0.#");
    private readonly TextBox _readings = new() { Watermark = "e.g. 1080 1095 1070", FontFamily = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace") };
    private readonly ComboBox _method = new();
    private readonly TextBox _job = new() { Watermark = "What the job was (file name, the piece) - kept with the record" };
    private readonly TextBox _notes = new() { Watermark = "Anything the advisor should know: where it was measured, cleaning passes, the blank" };
    private readonly TextBlock _state = Text("", 11.5, Muted), _error = Text("", 11.5, Amber);

    /// <summary>The study and coupon record written, when saved.</summary>
    public (Study Study, Coupon Coupon)? Result { get; private set; }

    public JobResultDialog(AblationDatabase db, MaterialSpec material, LaserSpec laser, string? lensName, PatchSettings? recipe, int passes)
    {
        _db = db; _material = material;
        // The laser as the job ran it: the preset, with the lens the sheet was written for.
        _laser = LensCatalog.Find(lensName) is { } lc
            ? laser with { Lens = lc.Name, FieldMm = lc.FieldMm > 0 ? lc.FieldMm : laser.FieldMm, SpotDiameterUm = lc.SpotUm ?? laser.SpotDiameterUm }
            : string.IsNullOrWhiteSpace(lensName) ? laser : laser with { Lens = lensName.Trim() };
        Title = "Record what this job did";
        Width = 700; MinWidth = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        var r = recipe ?? new PatchSettings();
        _power.Value = (decimal)r.PowerPercent; _speed.Value = (decimal)r.SpeedMmPerS; _density.Value = (decimal)r.LineDensityPerCm;
        _freq.Value = (decimal)r.FrequencyKHz; _pulse.Value = (decimal)(r.PulseWidthNs > 0 ? r.PulseWidthNs : 250); _layers.Value = Math.Max(1, passes);
        _focus.Value = (decimal)r.FocusOffsetMm; _hatch.Value = (decimal)r.HatchStepDeg;
        foreach (var m in MeasureDialog.Methods) _method.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        _method.SelectedIndex = 0;

        var p = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        p.Children.Add(new TextBlock { Text = $"{(string.IsNullOrEmpty(material.Alloy) ? material.Name : material.Summary)} on {_laser.Summary}", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Bright, TextWrapping = TextWrapping.Wrap });
        p.Children.Add(Text("You ran a job and measured it. Put down the settings exactly as the program ran them (the sheet's values are filled in - change any you changed) and the depth the job reached " +
                            "at a full-depth area of the design - where the map was black - after every layer ran, in micrometres from the untouched surface. " +
                            "The record is kept as one coupon in the study \"Jobs - " + (string.IsNullOrEmpty(material.Alloy) ? material.Name : material.Summary) + " on " + _laser.Name + "\" and counts as grade A, measured on this bench: " +
                            "the advice for this material uses it ahead of every reported figure. Edit or delete it later in the Ablation study window.", 11.5, Muted));

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("130,120,30,130,120"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        Cell(g, 0, 0, "Power %", _power, "Percent of the source's rating, as typed into the program.");
        Cell(g, 0, 3, "Speed mm/s", _speed, "Scan speed.");
        Cell(g, 1, 0, "Lines per cm", _density, "Line density (MakeIt) or 10 / line interval in mm (LightBurn).");
        Cell(g, 1, 3, "Frequency kHz", _freq, "Pulse repetition rate.");
        if (_laser.Kind == LaserKind.Mopa) Cell(g, 2, 0, "Pulse width ns", _pulse, "MOPA pulse width.");
        Cell(g, 2, 3, "Layers (passes)", _layers, "How many layers or passes the job ran. The depth below is after all of them.");
        Cell(g, 3, 0, "Focus offset mm", _focus, "Where focus was set relative to the surface when the job started: + above, - below, 0 on the surface. Recorded, not commanded.");
        Cell(g, 3, 3, "Hatch step °", _hatch, "How much the raster turned per layer (MakeIt's Auto Planning turns 27°; 0 if every layer ran along X).");
        p.Children.Add(new Border { Background = Panel, BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8), Child = g });

        var mr = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,30,130,120"), Margin = new Thickness(0, 4, 0, 0) };
        var rl = Text("Depth reached, µm", 12, Ink); rl.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_readings, "One or more readings at full-depth areas, separated by spaces or commas. The advisor uses their mean and keeps the spread.");
        mr.Children.Add(rl); Grid.SetColumn(_readings, 1); mr.Children.Add(_readings);
        var ml = Text("Measured with", 12, Ink); ml.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(ml, 3); mr.Children.Add(ml);
        Grid.SetColumn(_method, 4); mr.Children.Add(_method);
        p.Children.Add(mr);
        p.Children.Add(_state);
        p.Children.Add(Row("Job", _job));
        p.Children.Add(Row("Notes", _notes));
        p.Children.Add(_error);

        var save = MakeButton("Save as evidence", "Write the record into the ablation database. The advice is recomputed with it.");
        var cancel = MakeButton("Cancel", "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        p.Children.Add(buttons);
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        this.FitToScreen();

        _readings.TextChanged += (_, _) => UpdateState();
        UpdateState();
        save.Click += (_, _) => { if (Apply()) Close(true); };
        cancel.Click += (_, _) => Close(false);
    }

    private void UpdateState()
    {
        var r = MeasureDialog.Parse(_readings.Text);
        if (r is null) { _state.Text = "Readings must be numbers in micrometres."; _state.Foreground = Amber; return; }
        if (r.Count == 0) { _state.Text = "No depth yet."; _state.Foreground = Muted; return; }
        double mean = r.Average();
        int layers = (int)(_layers.Value ?? 1);
        _state.Text = $"Mean {mean:0.#} µm{(r.Count > 1 ? $" ({r.Min():0.#}–{r.Max():0.#})" : "")} over {layers} layer{(layers == 1 ? "" : "s")} = {mean / Math.Max(1, layers):0.##} µm per layer.";
        _state.Foreground = Green;
    }

    private bool Apply()
    {
        var r = MeasureDialog.Parse(_readings.Text);
        if (r is null || r.Count == 0) { _error.Text = "Enter the depth the job reached, in micrometres, before saving."; return false; }
        var settings = new PatchSettings
        {
            PowerPercent = (double)(_power.Value ?? 100), SpeedMmPerS = (double)(_speed.Value ?? 500), LineDensityPerCm = (double)(_density.Value ?? 300),
            FrequencyKHz = (double)(_freq.Value ?? 45), PulseWidthNs = _laser.Kind == LaserKind.Mopa ? (double)(_pulse.Value ?? 250) : 0,
            Layers = (int)(_layers.Value ?? 1), FocusOffsetMm = (double)(_focus.Value ?? 0), HatchStepDeg = (double)(_hatch.Value ?? 0),
        };
        var m = new PatchMeasurement
        {
            ReadingsUm = r, Method = (_method.SelectedItem as ComboBoxItem)?.Tag as string ?? "gauge",
            Notes = _notes.Text?.Trim() ?? "", MeasuredUtc = DateTimeOffset.UtcNow,
        };
        try
        {
            Result = _db.RecordJobResult(_material, _laser, settings, m, _job.Text ?? "", _notes.Text?.Trim() ?? "");
            _db.Save();
            return true;
        }
        catch (Exception ex) { _error.Text = "Could not save: " + ex.Message; return false; }
    }

    // ------------------------------------------------------------------ helpers

    private static void Cell(Grid g, int row, int col, string label, Control editor, string tip)
    {
        var t = Text(label, 12, Ink); t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 2, 8, 2);
        ToolTip.SetTip(t, tip); ToolTip.SetTip(editor, tip);
        editor.Margin = new Thickness(0, 2);
        Grid.SetRow(t, row); Grid.SetColumn(t, col); Grid.SetRow(editor, row); Grid.SetColumn(editor, col + 1);
        g.Children.Add(t); g.Children.Add(editor);
    }

    private static Control Row(string label, Control editor)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
        var t = Text(label, 12, Ink); t.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(t); Grid.SetColumn(editor, 1); g.Children.Add(editor);
        return g;
    }

    private static NumericUpDown Num(double min, double max, double step, string fmt) => new() { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = fmt, Width = 120 };
    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new() { Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"), BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }
}
