using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Ablation;

namespace DepthView.Views;

/// <summary>
/// The measure phase, by hand: the depth readings for every patch of an engraved coupon, in
/// micrometres relative to the untouched surface beside the patch. Several readings per patch
/// are kept as they are - the advisor uses their mean and shows their spread. A patch left
/// blank stays "engraved, not measured"; clearing a patch that had readings deletes that
/// evidence. A job-result record (one patch) is edited the same way. The gauge interface
/// (TODO §7.7b) will fill the same boxes; nothing here assumes it.
/// </summary>
public sealed class MeasureDialog : Window
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Amber = B("#F0C674"), Green = B("#5CC28A");

    public static readonly string[] Methods = ["gauge", "probe", "microscope", "caliper", "mass", "other"];

    private readonly Study _study;
    private readonly Coupon _coupon;
    private readonly ComboBox _method = new();
    private readonly TextBox _notes = new() { Watermark = "Notes kept with every reading entered now (the gauge, the reference surface, anything odd)", AcceptsReturn = false };
    private readonly List<(Patch Patch, TextBox Box, TextBlock State)> _rows = [];
    private readonly TextBlock _error = Text("", 11.5, Amber);

    /// <summary>True when something was saved.</summary>
    public bool Changed { get; private set; }

    public MeasureDialog(Study study, Coupon coupon)
    {
        _study = study; _coupon = coupon;
        Title = $"Measure {coupon.Name}";
        Width = 820; Height = 640; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        foreach (var m in Methods) _method.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        var existing = coupon.Patches.Select(p => p.Measurement?.Method).FirstOrDefault(m => !string.IsNullOrEmpty(m));
        _method.SelectedIndex = Math.Max(0, Array.IndexOf(Methods, existing ?? "gauge"));
        _notes.Text = coupon.Patches.Select(p => p.Measurement?.Notes).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";

        var p = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        p.Children.Add(new TextBlock { Text = $"{study.Name} - {coupon.Name}" + (coupon.Notes.Length > 0 ? $" ({coupon.Notes})" : ""), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Bright, TextWrapping = TextWrapping.Wrap });
        p.Children.Add(Text("Depth of each patch in micrometres (µm), measured from the untouched surface beside it down to the patch floor. " +
                            "Several readings on one patch go in the same box, separated by spaces or commas; the advisor uses their mean and keeps the spread. " +
                            "Leave a patch blank if it was not measured. Clearing a box that held readings deletes that evidence when you save. " +
                            "Everything saved here is grade A - measured on this bench - and outranks every reported figure for this material and laser.", 11.5, Muted));
        var mr = new Grid { ColumnDefinitions = new ColumnDefinitions("120,200,20,*") };
        var ml = Text("Measured with", 12, Ink); ml.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_method, "gauge: a dial or digital indicator on a stand or the CNC. probe: an electrical touch probe. microscope: a focus-stacking or confocal reading. caliper: a depth caliper or depth micrometer. mass: the piece weighed before and after, depth from the density and the area. The method travels with the readings.");
        mr.Children.Add(ml); Grid.SetColumn(_method, 1); mr.Children.Add(_method);
        Grid.SetColumn(_notes, 3); mr.Children.Add(_notes);
        p.Children.Add(mr);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,230,170,70"), Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (t, i) in new[] { ("Patch", 0), ("Settings", 1), ("Readings, µm", 2), ("On record", 3), ("", 4) })
        {
            var h = Text(t, 11, Muted, FontWeight.SemiBold); Grid.SetColumn(h, i); head.Children.Add(h);
        }
        p.Children.Add(head);
        var rows = new StackPanel { Spacing = 4 };
        foreach (var patch in coupon.Patches.OrderBy(x => x.Index))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,230,170,70") };
            var lab = Text(patch.Label.Length > 0 ? patch.Label : $"#{patch.Index + 1}", 12, Bright, FontWeight.SemiBold); lab.VerticalAlignment = VerticalAlignment.Center;
            var set = Text(patch.Settings.ToShortString(study.Laser.Kind) + (patch.Settings.FocusOffsetMm != 0 ? $" z{patch.Settings.FocusOffsetMm:+0.##;-0.##}" : ""), 11.5, Ink); set.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(set, $"Power {patch.Settings.PowerPercent:0.#} %, speed {patch.Settings.SpeedMmPerS:0.#} mm/s, {patch.Settings.LineDensityPerCm:0.#} lines/cm, {patch.Settings.FrequencyKHz:0.#} kHz" +
                                (study.Laser.Kind == LaserKind.Mopa ? $", {patch.Settings.PulseWidthNs:0.#} ns" : "") + $", {patch.Settings.Layers} layers. {patch.Reason}");
            var box = new TextBox { Watermark = "e.g. 412 418 409", FontFamily = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace"), Margin = new Thickness(0, 0, 8, 0) };
            if (patch.Measurement is { ReadingsUm.Count: > 0 } m) box.Text = string.Join(" ", m.ReadingsUm.Select(r => r.ToString("0.#", CultureInfo.InvariantCulture)));
            var state = Text("", 11, Muted); state.VerticalAlignment = VerticalAlignment.Center;
            var clear = MakeButton("Clear", "Empty the box. On save, the readings this patch had are deleted and it goes back to 'engraved, not measured'.");
            clear.Click += (_, _) => box.Text = "";
            Grid.SetColumn(set, 1); Grid.SetColumn(box, 2); Grid.SetColumn(state, 3); Grid.SetColumn(clear, 4);
            g.Children.Add(lab); g.Children.Add(set); g.Children.Add(box); g.Children.Add(state); g.Children.Add(clear);
            rows.Children.Add(g);
            _rows.Add((patch, box, state));
            box.TextChanged += (_, _) => UpdateState(patch, box, state);
            UpdateState(patch, box, state);
        }
        p.Children.Add(new Border { Background = Panel, BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8), Child = rows });
        p.Children.Add(_error);

        var save = MakeButton("Save readings", "Write the readings into the study. Patches with readings become measured; blank ones stay engraved; cleared ones lose their readings.");
        var cancel = MakeButton("Cancel", "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8) };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = p, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons);
        Content = grid;
        this.FitToScreen();

        save.Click += (_, _) => { if (Apply()) Close(true); };
        cancel.Click += (_, _) => Close(false);
    }

    /// <summary>Readings from a box: null when the text is not all numbers (the state text says so).</summary>
    public static List<double>? Parse(string? text)
    {
        var list = new List<double>();
        foreach (var tok in (text ?? "").Split([' ', ',', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v) || v < 0) return null;
            list.Add(v);
        }
        return list;
    }

    private void UpdateState(Patch patch, TextBox box, TextBlock state)
    {
        var r = Parse(box.Text);
        bool had = patch.Measurement is { ReadingsUm.Count: > 0 };
        if (r is null) { state.Text = "not a number"; state.Foreground = Amber; return; }
        if (r.Count == 0) { state.Text = had ? "readings will be deleted" : "not measured"; state.Foreground = had ? Amber : Muted; return; }
        double mean = r.Average();
        string spread = r.Count > 1 ? $" ({r.Min():0.#}–{r.Max():0.#})" : "";
        state.Text = $"mean {mean:0.#} µm{spread}, {r.Count} reading{(r.Count == 1 ? "" : "s")}";
        state.Foreground = Green;
    }

    private bool Apply()
    {
        string method = (_method.SelectedItem as ComboBoxItem)?.Tag as string ?? "gauge";
        string notes = _notes.Text?.Trim() ?? "";
        var updated = new List<Patch>(_coupon.Patches);
        bool changed = false;
        foreach (var (patch, box, _) in _rows)
        {
            var r = Parse(box.Text);
            if (r is null) { _error.Text = $"Patch {patch.Label}: readings must be numbers in micrometres, separated by spaces or commas."; return false; }
            int i = updated.FindIndex(x => ReferenceEquals(x, patch));
            if (i < 0) continue;
            bool had = patch.Measurement is { ReadingsUm.Count: > 0 };
            if (r.Count == 0)
            {
                if (!had) continue;
                updated[i] = patch with { Measurement = null, Status = patch.Status == PatchStatus.Measured ? PatchStatus.Engraved : patch.Status };
                changed = true;
                continue;
            }
            bool same = had && patch.Measurement!.ReadingsUm.SequenceEqual(r) && patch.Measurement.Method == method && patch.Measurement.Notes == notes;
            if (same) continue;
            updated[i] = patch with
            {
                Status = PatchStatus.Measured,
                Measurement = new PatchMeasurement { ReadingsUm = r, Method = method, Notes = notes, MeasuredUtc = DateTimeOffset.UtcNow },
            };
            changed = true;
        }
        if (changed) { _coupon.Patches = updated; Changed = true; }
        return true;
    }

    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new() { Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"), BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }
}
