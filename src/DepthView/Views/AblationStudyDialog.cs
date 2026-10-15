using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Ablation;
using DepthView.Controls;

namespace DepthView.Views;

/// <summary>
/// Sets up a study: material and alloy, laser and rating, the coupon every sheet or blank
/// must match, the patch grid, and the settings to test. The projection on the right updates
/// as the numbers change - how many settings squares a coupon holds, how many coupons the
/// design needs, how long each takes, and whether any setting is over the energy ceiling -
/// so the count of identical coupons is known and agreed to before anything is cut.
/// Returns the study, with its design generated, or null.
/// </summary>
public sealed class AblationStudyDialog : Window
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Amber = B("#F0C674"), Red = B("#EF6A70"), Green = B("#5CC28A");

    private readonly TextBox _name = new() { Watermark = "e.g. Brass H62 sheet, MOPA 100 W" };
    private readonly ComboBox _material = new(), _laser = new(), _shape = new();
    private readonly TextBox _alloy = new(), _lens = new(), _materialNotes = new() { Watermark = "Supplier, batch, surface state..." };
    private readonly NumericUpDown _density = Num(0.5, 25, 0.01, "0.00"), _rated = Num(1, 1000, 1, "0"), _spot = Num(1, 500, 1, "0"),
        _width = Num(5, 210, 0.1, "0.0"), _height = Num(5, 210, 0.1, "0.0"), _diameter = Num(5, 210, 0.1, "0.0"),
        _thick = Num(0.1, 50, 0.1, "0.0#"), _corner = Num(0, 50, 0.5, "0.0"),
        _patch = Num(1, 50, 0.5, "0.0"), _gap = Num(0.2, 20, 0.1, "0.0"), _margin = Num(0, 30, 0.5, "0.0"),
        _freq = Num(1, 4000, 1, "0"), _density2 = Num(10, 5000, 10, "0"), _layers = Num(1, 500, 1, "0"), _hatch = Num(0, 179, 1, "0"),
        _replicates = Num(1, 10, 1, "0"),
        _idPower = Num(1, 100, 1, "0"), _idSpeed = Num(10, 10000, 10, "0"), _idDensity = Num(10, 5000, 10, "0"), _idFreq = Num(1, 4000, 1, "0"), _idPulse = Num(1, 500, 1, "0"),
        _budget = Num(5, 95, 5, "0"), _rate = Num(1, 100, 1, "0");
    private readonly TextBox _powers = new() { Text = "100 70 50 35" }, _speeds = new() { Text = "300 600 1200 2400" }, _pulses = new() { Text = "250" };
    private readonly CheckBox _randomize = new() { Content = "Shuffle the cutting order (coupon heating then cannot become a trend)", IsChecked = true };
    private readonly CheckBox _makeIt = new() { Content = "Add a verification patch at the MakeIt relief defaults (100 %, 500 mm/s, 300/cm, 45 kHz, 250 ns)", IsChecked = true };
    private readonly TextBlock _projection = Text("", 12, Ink), _warn = Text("", 12, Amber), _stress = Text("", 12, Bright, FontWeight.SemiBold);
    private readonly CouponPreview _preview = new() { Height = 260 };
    private readonly Button _create;
    private bool _loading = true;

    public AblationStudyDialog(Study? template = null)
    {
        Title = "New ablation study";
        Width = 1180; Height = 860; MinWidth = 960; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        foreach (var m in MaterialSpec.Presets) _material.Items.Add(new ComboBoxItem { Content = m.Summary, Tag = m });
        foreach (var l in LaserSpec.Presets) _laser.Items.Add(new ComboBoxItem { Content = l.Summary, Tag = l });
        _shape.Items.Add(new ComboBoxItem { Content = "Sheet (rectangle)", Tag = CouponShape.Rectangle });
        _shape.Items.Add(new ComboBoxItem { Content = "Coin blank (round)", Tag = CouponShape.Coin });

        var t = template ?? new Study();
        _name.Text = t.Name;
        _material.SelectedIndex = Math.Max(0, MaterialSpec.Presets.ToList().FindIndex(m => m.Id == t.Material.Id));
        _alloy.Text = t.Material.Alloy; _density.Value = (decimal)t.Material.DensityGPerCm3; _materialNotes.Text = t.Material.Notes;
        _laser.SelectedIndex = Math.Max(0, LaserSpec.Presets.ToList().FindIndex(l => l.Id == t.Laser.Id));
        _rated.Value = (decimal)t.Laser.RatedPowerW; _spot.Value = (decimal)t.Laser.SpotDiameterUm; _lens.Text = t.Laser.Lens;
        _shape.SelectedIndex = t.Coupon.Shape == CouponShape.Coin ? 1 : 0;
        _width.Value = (decimal)t.Coupon.WidthMm; _height.Value = (decimal)t.Coupon.HeightMm; _diameter.Value = (decimal)t.Coupon.DiameterMm;
        _thick.Value = (decimal)t.Coupon.ThicknessMm; _corner.Value = (decimal)t.Coupon.CornerRadiusMm;
        _patch.Value = (decimal)t.PatchSizeMm; _gap.Value = (decimal)t.GapMm; _margin.Value = (decimal)t.MarginMm;
        _freq.Value = 45; _density2.Value = 300; _layers.Value = 10; _hatch.Value = 27; _replicates.Value = 2;
        _idPower.Value = (decimal)t.IdMark.PowerPercent; _idSpeed.Value = (decimal)t.IdMark.SpeedMmPerS; _idDensity.Value = (decimal)t.IdMark.LineDensityPerCm;
        _idFreq.Value = (decimal)t.IdMark.FrequencyKHz; _idPulse.Value = (decimal)t.IdMark.PulseWidthNs;
        _budget.Value = (decimal)(t.DepthBudgetFraction * 100); _rate.Value = (decimal)t.AssumedMaxRemovalUmPerJPerMm2;

        var left = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
        left.Children.Add(Head("Study"));
        left.Children.Add(Row("Name", _name, "A name for the list. The material and laser are recorded separately."));

        left.Children.Add(Head("Material"));
        left.Children.Add(Row("Material", _material, "Presets carry a density for turning weighed mass loss into depth. Edit the alloy to what the seller or an XRF reading says."));
        left.Children.Add(Row("Alloy / grade", _alloy, "H62, C260, 304... or 'unknown'. Never a guess dressed as a grade: 60/40 and 70/30 brass do not remove alike."));
        left.Children.Add(Row("Density g/cm³", _density, "Used by the mass-loss method: depth = mass / (density × area)."));
        left.Children.Add(Row("Notes", _materialNotes, "Supplier, batch, surface finish - anything a later reader would want to know."));

        left.Children.Add(Head("Laser"));
        left.Children.Add(Row("Laser", _laser, "The source the study is cut on. Its rating in watts is what every figure is derived from: 50 % of a 60 W source is 30 W, of a 100 W source 50 W."));
        left.Children.Add(Row("Rated power W", _rated, "The source's rating. Results are stored against watts, so they can be carried to another rating by watts."));
        left.Children.Add(Row("Spot µm", _spot, "What the fluence figures assume. Measure it (the calibration coupon) rather than trusting the lens label."));
        left.Children.Add(Row("Lens", _lens, "Which lens was fitted. Removal efficiency is calibrated per lens."));

        left.Children.Add(Head("Coupon - every one identical"));
        left.Children.Add(Row("Shape", _shape, "A rectangle of sheet, or a round blank."));
        left.Children.Add(Row("Width mm", _width, "Sheet width. Keep the coupon inside the laser's field."));
        left.Children.Add(Row("Height mm", _height, "Sheet height."));
        left.Children.Add(Row("Diameter mm", _diameter, "The blank's diameter."));
        left.Children.Add(Row("Thickness mm", _thick, "Sets the energy ceiling: no patch may receive enough energy to remove more than the depth budget below."));
        left.Children.Add(Row("Corner radius mm", _corner, "Rounded corners of sheet stock, for the drawing only."));

        left.Children.Add(Head("Patches"));
        left.Children.Add(Row("Patch size mm", _patch, "The square each setting cuts. 5 mm suits a 2 mm ball gauge tip; a probe needle allows smaller."));
        left.Children.Add(Row("Gap mm", _gap, "Untouched metal between patches. The reading taken here, beside each patch, is the local zero that cancels the coupon's bow - never less than the gauge tip needs."));
        left.Children.Add(Row("Margin mm", _margin, "Clear metal around the grid, where the crosses and the digits go."));

        left.Children.Add(Head("Settings to test"));
        left.Children.Add(Row("Powers %", _powers, "Power levels, separated by spaces. Each is cut at every speed below."));
        left.Children.Add(Row("Speeds mm/s", _speeds, "Speeds, separated by spaces. Power and speed together sweep the dose per layer; speed also sets the pulse overlap."));
        left.Children.Add(Row("Pulse widths ns", _pulses, "MOPA only: each pulse width repeats the whole power × speed grid. Removal efficiency depends on it strongly."));
        left.Children.Add(Row("Frequency kHz", _freq, "Pulse frequency for every patch. Pulse energy is power ÷ frequency."));
        left.Children.Add(Row("Line density /cm", _density2, "Lines per centimetre, as MakeIt counts them (300 = 0.033 mm apart)."));
        left.Children.Add(Row("Layers", _layers, "Layers per patch, all at one focus. Enough that the depth is well above the gauge's graduation; few enough to stay shallow next to the depth of focus - 150 to 300 µm is the aim."));
        left.Children.Add(Row("Hatch step °", _hatch, "The raster turns this much each layer (MakeIt's Auto Planning turns 27°). 0 keeps every layer along X."));
        left.Children.Add(Row("Replicates", _replicates, "How many times each setting is cut. Two or three tell the noise floor apart from an effect; one cannot."));
        left.Children.Add(_randomize);
        left.Children.Add(_makeIt);

        left.Children.Add(Head("Test-number mark"));
        left.Children.Add(Text("The QR code and digits are a dark mark, not depth. A starting point for brass; tune it on scrap.", 11, Muted));
        var idRow = new WrapPanel();
        idRow.Children.Add(Small("Power %", _idPower)); idRow.Children.Add(Small("Speed", _idSpeed)); idRow.Children.Add(Small("Lines/cm", _idDensity));
        idRow.Children.Add(Small("kHz", _idFreq)); idRow.Children.Add(Small("ns", _idPulse));
        left.Children.Add(idRow);

        left.Children.Add(Head("Energy ceiling"));
        left.Children.Add(Row("Depth budget %", _budget, "Share of the thickness no patch may be allowed to remove, even at the removal rate below."));
        left.Children.Add(Row("Max removal µm per J/mm²", _rate, "A removal rate deliberately above anything these metals have shown (brass runs near 2). The ceiling is budget ÷ rate; a measured rate can tighten it."));

        var right = new StackPanel { Spacing = 8, Margin = new Thickness(14) };
        right.Children.Add(Head("Projection"));
        right.Children.Add(_preview);
        right.Children.Add(_stress);
        right.Children.Add(_projection);
        right.Children.Add(_warn);

        _create = MakeButton("Create study", "Creates the study with this design. Nothing is sent to the laser; coupons are planned one at a time from the study.");
        var cancel = MakeButton("Cancel", "Close without creating a study.");
        _create.Click += (_, _) => Close(BuildStudy());
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 8) };
        buttons.Children.Add(cancel); buttons.Children.Add(_create);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("560,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer { Content = left, Background = Panel };
        grid.Children.Add(scroll);
        Grid.SetColumn(right, 1);
        grid.Children.Add(new ScrollViewer { Content = right });
        Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;

        foreach (var n in new[] { _density, _rated, _spot, _width, _height, _diameter, _thick, _corner, _patch, _gap, _margin, _freq, _density2, _layers, _hatch, _replicates, _idPower, _idSpeed, _idDensity, _idFreq, _idPulse, _budget, _rate })
            n.ValueChanged += (_, _) => Refresh();
        foreach (var tb in new[] { _powers, _speeds, _pulses, _alloy })
            tb.TextChanged += (_, _) => Refresh();
        _material.SelectionChanged += (_, _) => { if (!_loading && Selected<MaterialSpec>(_material) is { } m) { _alloy.Text = m.Alloy; _density.Value = (decimal)m.DensityGPerCm3; _materialNotes.Text = m.Notes; } Refresh(); };
        _laser.SelectionChanged += (_, _) => { if (!_loading && Selected<LaserSpec>(_laser) is { } l) { _rated.Value = (decimal)l.RatedPowerW; _spot.Value = (decimal)l.SpotDiameterUm; _lens.Text = l.Lens; _pulses.IsEnabled = _idPulse.IsEnabled = l.Kind == LaserKind.Mopa; } Refresh(); };
        _shape.SelectionChanged += (_, _) => Refresh();
        _randomize.IsCheckedChanged += (_, _) => Refresh();
        _makeIt.IsCheckedChanged += (_, _) => Refresh();
        _loading = false;
        Refresh();
    }

    private static T? Selected<T>(ComboBox box) where T : class => (box.SelectedItem as ComboBoxItem)?.Tag as T;

    private MaterialSpec Material()
    {
        var m = Selected<MaterialSpec>(_material) ?? MaterialSpec.Presets[0];
        string alloy = (_alloy.Text ?? "").Trim();
        return m with { Alloy = alloy.Length == 0 ? "unknown" : alloy, DensityGPerCm3 = (double)(_density.Value ?? 8.5m), Notes = _materialNotes.Text ?? "",
                        Id = $"{m.Name.ToLowerInvariant().Replace(' ', '-')}-{(alloy.Length == 0 ? "unknown" : alloy.ToLowerInvariant())}" };
    }

    private LaserSpec Laser()
    {
        var l = Selected<LaserSpec>(_laser) ?? LaserSpec.Presets[0];
        return l with { RatedPowerW = (double)(_rated.Value ?? 100), SpotDiameterUm = (double)(_spot.Value ?? 40), Lens = _lens.Text ?? l.Lens };
    }

    private CouponSpec Coupon() => new()
    {
        Shape = Selected<object>(_shape) is CouponShape s ? s : CouponShape.Rectangle,
        WidthMm = (double)(_width.Value ?? 100), HeightMm = (double)(_height.Value ?? 100), DiameterMm = (double)(_diameter.Value ?? 40),
        ThicknessMm = (double)(_thick.Value ?? 3), CornerRadiusMm = (double)(_corner.Value ?? 0),
    };

    private DesignOptions Options(LaserSpec laser)
    {
        var o = new DesignOptions
        {
            PowerPercents = Numbers(_powers.Text), SpeedsMmPerS = Numbers(_speeds.Text), PulseWidthsNs = Numbers(_pulses.Text),
            FrequencyKHz = (double)(_freq.Value ?? 45), LineDensityPerCm = (double)(_density2.Value ?? 300), Layers = (int)(_layers.Value ?? 10),
            HatchStepDeg = (double)(_hatch.Value ?? 27), Replicates = (int)(_replicates.Value ?? 2), Randomize = _randomize.IsChecked == true,
        };
        if (_makeIt.IsChecked == true)
            o.Verification.Add(new PatchSettings { PowerPercent = 100, SpeedMmPerS = 500, LineDensityPerCm = 300, FrequencyKHz = 45, PulseWidthNs = laser.Kind == LaserKind.Mopa ? 250 : 0, Layers = o.Layers, HatchStepDeg = 27 });
        return o;
    }

    private static List<double> Numbers(string? text) =>
        (text ?? "").Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)
            .Where(v => double.IsFinite(v) && v > 0).ToList();

    private Study BuildStudy()
    {
        var laser = Laser();
        var s = new Study
        {
            Name = string.IsNullOrWhiteSpace(_name.Text) ? $"{Material().Summary} on {laser.RatedPowerW:0} W {laser.Kind}" : _name.Text!.Trim(),
            Material = Material(), Laser = laser, Coupon = Coupon(),
            PatchSizeMm = (double)(_patch.Value ?? 5), GapMm = (double)(_gap.Value ?? 1.5m), MarginMm = (double)(_margin.Value ?? 2),
            IdMark = new PatchSettings
            {
                PowerPercent = (double)(_idPower.Value ?? 60), SpeedMmPerS = (double)(_idSpeed.Value ?? 400), LineDensityPerCm = (double)(_idDensity.Value ?? 500),
                FrequencyKHz = (double)(_idFreq.Value ?? 30), PulseWidthNs = laser.Kind == LaserKind.Mopa ? (double)(_idPulse.Value ?? 200) : 0, Layers = 1, HatchStepDeg = 0,
            },
            DepthBudgetFraction = (double)(_budget.Value ?? 50m) / 100.0, AssumedMaxRemovalUmPerJPerMm2 = (double)(_rate.Value ?? 10),
        };
        var (design, reasons) = StudyDesign.Build(Options(laser), laser, out _);
        s.Design = design;
        s.DesignReasons = reasons;
        return s;
    }

    private void Refresh()
    {
        if (_loading) return;
        bool coin = Selected<object>(_shape) is CouponShape.Coin;
        _width.IsEnabled = _height.IsEnabled = _corner.IsEnabled = !coin;
        _diameter.IsEnabled = coin;
        var s = BuildStudy();
        var layout = s.Layout;
        _preview.Show(s, null);
        _ = StudyDesign.Build(Options(s.Laser), s.Laser, out var skipped);
        int needed = s.CouponsFor(s.Design.Count);
        var demo = new Coupon();
        for (int i = 0; i < Math.Min(layout.Capacity, s.Design.Count); i++)
            demo.Patches.Add(new Patch { Index = i, Settings = s.Design[i] });
        var time = CouponJob.Estimate(s, demo);
        double ceiling = s.EnergyCeilingJPerMm2;
        var hot = CouponJob.TooHot(s, s.Design.Select((d, i) => new Patch { Label = $"#{i + 1}", Settings = d }));
        double maxDose = s.Design.Count > 0 ? s.Design.Max(d => d.TotalDoseJPerMm2(s.Laser.RatedPowerW)) : 0;

        _stress.Text = layout.Capacity <= 0
            ? "No patch fits this coupon: make the patch smaller or the margin narrower."
            : $"{needed} coupon{(needed == 1 ? "" : "s")} needed, every one exactly {s.Coupon.Summary.ToLowerInvariant()}.";
        _projection.Text = string.Join("\n", new[]
        {
            layout.Summary + ".",
            $"{s.Design.Count} settings to cut ({Options(s.Laser).Replicates} replicate{(Options(s.Laser).Replicates == 1 ? "" : "s")}); the last coupon holds {(s.Design.Count % Math.Max(1, layout.Capacity) == 0 ? layout.Capacity : s.Design.Count % Math.Max(1, layout.Capacity))}.",
            $"About {time.TotalMinutes:0} min of cutting per full coupon.",
            $"Energy ceiling {ceiling:0} J/mm² per patch ({s.DepthBudgetFraction * 100:0}% of {s.Coupon.ThicknessMm:0.#} mm at {s.AssumedMaxRemovalUmPerJPerMm2:0} µm per J/mm²); the hottest setting delivers {maxDose:0.#} J/mm².",
            $"QR modules {TestNumberMark.ModuleMm(1, s.PatchSizeMm):0.00} mm{(TestNumberMark.ModuleMm(1, s.PatchSizeMm) < 0.3 ? " - a phone will struggle; a USB microscope reads it, and the digits are engraved too" : "")}.",
        });
        var warn = new List<string>();
        if (hot.Count > 0) warn.Add($"{hot.Count} setting{(hot.Count == 1 ? "" : "s")} over the ceiling (hottest {hot[0].JPerMm2:0} J/mm²): fewer layers, less power or a slower dose - the study will not cut them.");
        if (skipped.Count > 0) warn.Add(string.Join(" ", skipped.Distinct()));
        if (s.GapMm < 1.5) warn.Add("A gap under 1.5 mm leaves no room for a 2 mm ball tip to take the local zero beside each patch.");
        if (s.Coupon.BoxWidthMm > s.Laser.FieldMm || s.Coupon.BoxHeightMm > s.Laser.FieldMm) warn.Add($"The coupon is larger than the {s.Laser.FieldMm:0} mm field: cut it down, or the job cannot reach every patch.");
        _warn.Text = string.Join("\n", warn);
        _warn.Foreground = hot.Count > 0 ? Red : Amber;
        _create.IsEnabled = layout.Capacity > 0 && s.Design.Count > 0 && hot.Count == 0;
    }

    // ------------------------------------------------------------------ helpers

    private static Control Row(string label, Control editor, string tip)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        var t = Text(label, 12, Ink);
        t.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(t, tip);
        ToolTip.SetTip(editor, tip);
        g.Children.Add(t);
        Grid.SetColumn(editor, 1);
        g.Children.Add(editor);
        return g;
    }

    private static Control Small(string label, NumericUpDown n)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 10, 4) };
        var t = Text(label, 11, Muted); t.VerticalAlignment = VerticalAlignment.Center;
        n.Width = 90;
        p.Children.Add(t); p.Children.Add(n);
        return p;
    }

    private static TextBlock Head(string s) => new() { Text = s, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = B("#9FB4FF"), Margin = new Thickness(0, 10, 0, 2) };

    private static NumericUpDown Num(double min, double max, double step, string fmt) => new()
    {
        Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = fmt, ShowButtonSpinner = false,
    };

    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
    };

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Button MakeButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"),
            BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }
}
