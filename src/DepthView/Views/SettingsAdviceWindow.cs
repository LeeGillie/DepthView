using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Ablation;

namespace DepthView.Views;

/// <summary>
/// Settings for a job from the ablation study's evidence: material, laser and target depth
/// in; a MakeIt recipe, the predicted depth as a band with its grade, the Z advance, and
/// every note and warning out. Opened from the Tune window's JOB section. Shows what the
/// evidence supports and refuses the rest, exactly as <see cref="SettingsAdvisor"/> does.
/// </summary>
public sealed class SettingsAdviceWindow : Window
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Amber = B("#F0C674"), Green = B("#5CC28A");

    private readonly AblationDatabase _db;
    private readonly ComboBox _material = new(), _laser = new();
    private readonly TextBox _lens = new();
    private readonly NumericUpDown _target = new() { Minimum = 0.01m, Maximum = 20, Increment = 0.05m, FormatString = "0.00", Width = 110 };
    private readonly NumericUpDown _passes = new() { Minimum = 1, Maximum = 65535, Increment = 16, FormatString = "0", Width = 110 };
    private readonly TextBlock _verdict = Text("", 13, Bright, FontWeight.SemiBold), _grade = Text("", 12, Ink), _recipe = Text("", 12, Ink),
        _notes = Text("", 11, Muted), _warn = Text("", 12, Amber), _used = Text("", 11, Muted);
    private readonly Button _copy, _close;
    private SettingsAdvice? _advice;

    public SettingsAdviceWindow(AblationDatabase db, string? materialName, LaserKind? kind, string? lens, double targetDepthMm, int passes)
    {
        _db = db;
        Title = "Settings from the ablation study";
        Width = 760; Height = 720; MinWidth = 600; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        foreach (var m in MaterialSpec.Presets) _material.Items.Add(new ComboBoxItem { Content = m.Summary, Tag = m });
        foreach (var l in LaserSpec.Presets) _laser.Items.Add(new ComboBoxItem { Content = l.Summary, Tag = l });
        // The finishing recipe names its metal with one lowercase word ("brass", "stainless"); match the preset whose name starts with it.
        int mi = string.IsNullOrWhiteSpace(materialName) ? -1
            : MaterialSpec.Presets.ToList().FindIndex(m => m.Name.StartsWith(materialName.Trim(), StringComparison.OrdinalIgnoreCase) || materialName.Trim().StartsWith(m.Name, StringComparison.OrdinalIgnoreCase));
        _material.SelectedIndex = Math.Max(0, mi);
        int li = kind is { } k ? LaserSpec.Presets.ToList().FindIndex(l => l.Kind == k) : 0;
        _laser.SelectedIndex = Math.Max(0, li);
        _lens.Text = lens ?? "";
        _target.Value = (decimal)Math.Clamp(targetDepthMm, 0.01, 20);
        _passes.Value = Math.Max(1, passes);

        _copy = MakeButton("Copy the recipe", "Copy the settings as text, to type into MakeIt.");
        _close = MakeButton("Close", "");

        var p = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        p.Children.Add(Head("The job"));
        p.Children.Add(Text("Everything below comes from the ablation study's evidence - coupons measured on this bench when there are any, the reference studies' reported figures otherwise - and says which. Nothing is extrapolated beyond what was tested.", 11, Muted));
        p.Children.Add(Row("Material", _material, "The metal of the blank. Removal is calibrated per material and alloy."));
        p.Children.Add(Row("Laser", _laser, "The source the job will run on; settings are given as percent of its rating."));
        p.Children.Add(Row("Lens", _lens, "As named in the Tune window. Removal is calibrated per lens; a different lens is noted."));
        var tr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tr.Children.Add(Text("Target depth mm", 12, Ink)); tr.Children.Add(_target);
        tr.Children.Add(Text("in passes", 12, Ink)); tr.Children.Add(_passes);
        p.Children.Add(tr);

        p.Children.Add(Head("Advice"));
        p.Children.Add(_verdict);
        p.Children.Add(_grade);
        _recipe.FontFamily = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace");
        var box = new Border { Background = Panel, BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8), Child = _recipe };
        p.Children.Add(box);
        p.Children.Add(_warn);
        p.Children.Add(_notes);
        p.Children.Add(Head("Evidence used"));
        p.Children.Add(_used);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8) };
        buttons.Children.Add(_copy); buttons.Children.Add(_close);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(buttons, 1); grid.Children.Add(buttons);
        Content = grid;
        this.FitToScreen();

        _material.SelectionChanged += (_, _) => Refresh();
        _laser.SelectionChanged += (_, _) => Refresh();
        _lens.TextChanged += (_, _) => Refresh();
        _target.ValueChanged += (_, _) => Refresh();
        _passes.ValueChanged += (_, _) => Refresh();
        _copy.Click += async (_, _) => { if (_advice?.Recipe is not null && Clipboard is { } cb) await cb.SetTextAsync(RecipeText(_advice)); };
        _close.Click += (_, _) => Close();
        Refresh();
    }

    private void Refresh()
    {
        if ((_material.SelectedItem as ComboBoxItem)?.Tag is not MaterialSpec m || (_laser.SelectedItem as ComboBoxItem)?.Tag is not LaserSpec l) return;
        double target = (double)(_target.Value ?? 1);
        int passes = (int)(_passes.Value ?? 256);
        _advice = SettingsAdvisor.Advise(_db, m, l, target, passes, _lens.Text);
        var a = _advice;
        _verdict.Text = a.Verdict;
        _verdict.Foreground = a.HasRecipe && a.PredictedPerPassUm is not null ? (a.Grade == "A" ? Green : Bright) : Amber;
        _grade.Text = $"Grade of this advice: {a.GradeText}." + (a.RayleighMm is { } z ? $" Estimated Rayleigh range of the lens: {z:0.##} mm." : "");
        _recipe.Text = a.Recipe is null ? "No recipe: nothing in the evidence applies to this material on this kind of source." : RecipeText(a);
        _warn.Text = string.Join("\n", a.Warnings.Select(w => "⚠ " + w));
        _warn.IsVisible = a.Warnings.Count > 0;
        _notes.Text = string.Join("\n", a.Notes);
        _used.Text = a.Used.Count == 0 ? "None - see above." : string.Join("\n", a.Used.Select(e =>
            $"• {e.Source} (grade {e.Grade}, {e.Machine}): {e.Settings.ToShortString(e.Laser.Kind)} → {e.DepthUm:0} µm ({e.DepthLowUm:0}–{e.DepthHighUm:0}) over {e.Layers} layer{(e.Layers == 1 ? "" : "s")}, η {e.Eta:0.##} µm per J/mm²"));
        _copy.IsEnabled = a.Recipe is not null;
    }

    /// <summary>The recipe in MakeIt's words and units, with its provenance on the same lines.</summary>
    public static string RecipeText(SettingsAdvice a)
    {
        if (a.Recipe is not { } r) return "";
        var l = a.Laser;
        string z = a.ZAdvanceMm is { } za ? $"{za:0.0000} mm (the predicted removal per pass, so focus follows the floor)" : "not predicted (no depth evidence)";
        bool spread = a.PredictedPerPassHighUm - a.PredictedPerPassLowUm > 0.05;
        string depth = a.PredictedPerPassUm is { } d
            ? $"{d:0.#} µm per pass{(spread ? $" ({a.PredictedPerPassLowUm:0.#}–{a.PredictedPerPassHighUm:0.#})" : "")}; {a.PredictedTotalMm:0.00} mm in {a.Passes} passes{(spread ? $" ({a.PredictedTotalLowMm:0.00}–{a.PredictedTotalHighMm:0.00})" : " (one reported figure, no spread)")}"
            : "not predicted - uncalibrated";
        return $"MakeIt settings for {a.MaterialName} on {l.Name} ({l.RatedPowerW:0} W, {l.Lens}):\n" +
               $"  Power            {r.PowerPercent:0.#} %  ({r.PowerW(l.RatedPowerW):0.#} W)\n" +
               $"  Speed            {r.SpeedMmPerS:0} mm/s\n" +
               $"  Line density     {r.LineDensityPerCm:0} lines/cm  ({r.LinePitchMm:0.000} mm)\n" +
               $"  Frequency        {r.FrequencyKHz:0} kHz\n" +
               (l.Kind == LaserKind.Mopa ? $"  Pulse width      {r.PulseWidthNs:0} ns\n" : "") +
               $"  Layers           {a.Passes}\n" +
               $"  Descent/layer    {z}\n" +
               $"  Dose             {r.DosePerLayerJPerMm2(l.RatedPowerW):0.00} J/mm² per pass\n" +
               $"  Predicted depth  {depth}\n" +
               $"  Grade            {a.GradeText}";
    }

    // ------------------------------------------------------------------ helpers

    private static Control Row(string label, Control editor, string tip)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };
        var t = Text(label, 12, Ink); t.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(t, tip); ToolTip.SetTip(editor, tip);
        g.Children.Add(t); Grid.SetColumn(editor, 1); g.Children.Add(editor);
        return g;
    }

    private static TextBlock Head(string s) => new() { Text = s, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = B("#9FB4FF"), Margin = new Thickness(0, 10, 0, 2) };
    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new() { Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"), BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }
}
