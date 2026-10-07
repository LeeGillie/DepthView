using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepthView.Controls;
using DepthView.Finishing;
using DepthView.Processing;
using DepthView.Rendering;

namespace DepthView.Views;

/// <summary>
/// The finishing preview: the tuned map as it would look after cleaning, darkening,
/// relieving and sealing, with what is known about each step one hover or one click away.
///
/// Advice is never in the way. Every choice carries a tooltip; the guidance panel follows
/// whichever step was last touched and can be folded away; safety badges sit beside the
/// darkener because they matter whether or not anyone opens the panel; and the checks under
/// the picture only speak when the recipe has a problem.
///
/// Built in code like GcodeWindow, for the same reason: no InitializeComponent trap.
/// </summary>
public sealed class FinishWindow : Window
{
    private static readonly IBrush Bg = Brush("#101215"), Panel = Brush("#171A1F"), Line = Brush("#262B33"),
        Ink = Brush("#DDE3EA"), Bright = Brush("#EDF1F6"), Muted = Brush("#8A929E"), Faint = Brush("#5E6773"),
        Amber = Brush("#F0C674"), Accent = Brush("#2F6FB3"), Chip = Brush("#22262D");

    /// <summary>The last recipe and panel state, kept for the session so reopening picks up where you were.</summary>
    private static FinishRecipe _lastRecipe = new();
    private static bool _guideOpen = true;

    private readonly FinishCatalogue _cat = FinishCatalogue.Builtin;
    private readonly FinishRecipe _r;

    private readonly ushort[] _grey;
    private readonly int _w, _h, _maxValue;
    private readonly TuningOptions _full;
    private readonly string _fileName;
    private readonly double _blankMm, _depthMm, _thickMm;

    private float[]? _field;
    private int _fw, _fh;

    private readonly ReliefPreview _preview = new();
    private readonly TextBlock _status = Text("", 11, Muted);
    private readonly StackPanel _checks = new() { Spacing = 3 };
    private readonly List<Button> _chips = new();

    private readonly ComboBox _material = new(), _clean = new(), _prepolish = new(), _darkener = new(),
        _tool = new(), _pressure = new(), _sealer = new();
    private readonly Slider _strength = new() { Minimum = 5, Maximum = 100, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _strengthText = Text("", 11, Ink);
    private readonly NumericUpDown _darkMinutes = Minutes(), _rubMinutes = Minutes();
    private readonly Border _hazard = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _exag = new() { Minimum = 0, Maximum = 3, SmallChange = 0.5, LargeChange = 1, TickFrequency = 0.5, IsSnapToTickEnabled = true, Value = 1, Width = 120 };
    private readonly TextBlock _exagText = Text("", 11, Muted);

    private readonly Expander _guide = new() { Header = "Guidance", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _gTitle = Text("", 13, Bright, FontWeight.SemiBold);
    private readonly TextBlock _gAdvice = Text("", 12, Ink);
    private readonly TextBlock _gNumbers = Text("", 11, Muted);
    private readonly TextBlock _gBasis = Text("", 11, Faint);
    private readonly Border _gHazard = new() { CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 6), IsVisible = false };
    private readonly WrapPanel _gSources = new();

    private bool _loading = true;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _gen;
    private FinishResult? _last;

    public FinishWindow(ushort[] grey, int w, int h, int maxValue, TuningOptions full, string fileName,
                        FinishRecipe? recipe = null)
    {
        _grey = grey; _w = w; _h = h; _maxValue = maxValue; _full = full; _fileName = fileName;
        _blankMm = Blank.Current.DiameterMm;
        _depthMm = Blank.Current.TargetDepthMm;
        _thickMm = Blank.Current.ThicknessMm;
        _r = (recipe ?? _lastRecipe).Clone();

        Title = $"Finishing - {fileName}";
        Width = 1320; Height = 860; MinWidth = 900; MinHeight = 560;
        Background = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://DepthView/Assets/depthview-icon-256.png"))); }
        catch { /* the icon is cosmetic */ }

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Simulate(); };

        Content = BuildLayout();
        Populate();
        if (recipe is not null) ShowStep(_r.StopAfter);
        else ShowGeneral();
        _guide.IsExpanded = _guideOpen;
        _guide.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) _guideOpen = _guide.IsExpanded; };

        Opened += async (_, _) => await PrepareAsync();
        Closed += (_, _) => { _cts?.Cancel(); _lastRecipe = _r.Clone(); };
    }

    // ------------------------------------------------------------------ layout

    private Control BuildLayout()
    {
        var title = Text($"Finishing preview   {_fileName}", 15, Bright, FontWeight.SemiBold);
        var sub = Text("What cleaning, darkening, relieving and sealing would do to this relief. A look, not a prediction: "
                     + "colours, roughness and tool reach are estimates from published practice. Calibrate on a test coin.", 11, Faint);
        var notes = MakeButton("Research notes", "The research behind every number in this window, with its sources. Opens in your browser.");
        notes.Click += async (_, _) =>
        {
            if (await Guides.OpenAsync(GetTopLevel(this), Guides.Kind.Finishing) is { } problem) _status.Text = problem;
        };
        var headGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headGrid.Children.Add(new StackPanel { Spacing = 2, Children = { title, sub } });
        Grid.SetColumn(notes, 1);
        notes.VerticalAlignment = VerticalAlignment.Center;
        headGrid.Children.Add(notes);
        var header = new Border
        {
            Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10), Child = headGrid,
        };

        // Stage chips: show the coin after any step.
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chipRow.Children.Add(Text("Show after", 11, Muted));
        string[] tips =
        {
            "Straight off the laser: powder and dark oxide in the engraving.",
            "After cleaning: bare metal where the oxide came off.",
            "After pre-polishing, before any darkener.",
            "Darkened all over - the step everyone is tempted to skip to.",
            "After rubbing the patina back off the highs.",
            "Finished and sealed.",
        };
        foreach (var st in Enum.GetValues<FinishStage>())
        {
            var b = new Button
            {
                Content = st == FinishStage.Polish ? "Pre-polish" : st.ToString(),
                Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(12),
                Background = Chip, Foreground = Ink, BorderBrush = Brush("#333944"), BorderThickness = new Thickness(1),
                FontSize = 12, Tag = st,
            };
            ToolTip.SetTip(b, tips[(int)st]);
            b.Click += (_, _) => { _r.StopAfter = (FinishStage)b.Tag!; SyncChips(); Queue(); };
            _chips.Add(b);
            chipRow.Children.Add(b);
        }
        foreach (var c in chipRow.Children.OfType<TextBlock>()) c.VerticalAlignment = VerticalAlignment.Center;

        // View controls under the picture.
        var reset = MakeButton("Reset view", "Back to the starting camera. Drag to orbit, Ctrl+drag moves the light, right-drag pans, wheel zooms.");
        reset.Click += (_, _) => { _preview.Settings.ResetView(); _preview.Request(false); };
        var top = MakeButton("Top-down", "Switch between looking straight down and the tilted 3D view.");
        top.Click += (_, _) =>
        {
            _preview.Settings.Orbit = !_preview.Settings.Orbit;
            top.Content = _preview.Settings.Orbit ? "Top-down" : "3D";
            _preview.Request(false);
        };
        var save = MakeButton("Save picture...", "Save this view as a PNG at 1800 px wide.");
        save.Click += async (_, _) => await SavePictureAsync();
        ToolTip.SetTip(_exag, "How deep the relief is drawn. 0 is true scale; each step doubles it. Finishing reads better slightly exaggerated, as the eye does with a coin in the hand.");
        _exag.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncDepth(); };
        var viewRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Text("Depth", 11, Muted), _exag, _exagText, top, reset, save },
        };
        foreach (var c in viewRow.Children) c.VerticalAlignment = VerticalAlignment.Center;

        var checksBox = new Border
        {
            Background = Brush("#14171B"), BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7),
            Child = new StackPanel { Spacing = 4, Children = { _checks, _status } },
        };

        var pictureFrame = new Border
        {
            Background = Brush("#0C0E11"), BorderBrush = Brush("#2A303A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = _preview,
        };
        var left = new DockPanel { Margin = new Thickness(14, 12, 7, 12) };
        DockPanel.SetDock(chipRow, Dock.Top);
        chipRow.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(checksBox, Dock.Bottom);
        DockPanel.SetDock(viewRow, Dock.Bottom);
        viewRow.Margin = new Thickness(0, 8, 0, 8);
        left.Children.Add(chipRow);
        left.Children.Add(checksBox);
        left.Children.Add(viewRow);
        left.Children.Add(pictureFrame);

        // Right: the recipe, one section per step, then the guidance panel.
        var steps = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 4, 0) };
        steps.Children.Add(Section(FinishStage.Raw, "Material", Row("Metal", _material)));
        steps.Children.Add(Section(FinishStage.Clean, "1  Clean", Row("Method", _clean)));
        steps.Children.Add(Section(FinishStage.Polish, "2  Pre-polish", Row("Tool", _prepolish)));

        var darkTop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _darkener.HorizontalAlignment = HorizontalAlignment.Stretch;
        darkTop.Children.Add(_darkener);
        Grid.SetColumn(_hazard, 1);
        _hazard.Margin = new Thickness(6, 0, 0, 0);
        darkTop.Children.Add(_hazard);
        var strengthRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,64") };
        strengthRow.Children.Add(_strength);
        Grid.SetColumn(_strengthText, 1);
        _strengthText.VerticalAlignment = VerticalAlignment.Center;
        _strengthText.Margin = new Thickness(8, 0, 0, 0);
        strengthRow.Children.Add(_strengthText);
        ToolTip.SetTip(_strength, "Strength as a share of the product neat. Diluting slows it and moves the colour toward the browns; 35-50% is the usual advice for an even black.");
        ToolTip.SetTip(_darkMinutes, "Dwell time in the solution, or before rinsing off a brushed coat.");
        steps.Children.Add(Section(FinishStage.Darken, "3  Darken",
            Row("Product", darkTop), Row("Strength", strengthRow), Row("Minutes", _darkMinutes)));

        ToolTip.SetTip(_rubMinutes, "Time spent rubbing back. Longer takes more off, and further down.");
        steps.Children.Add(Section(FinishStage.Relieve, "4  Relieve the highs",
            Row("Tool", _tool), Row("Pressure", _pressure), Row("Minutes", _rubMinutes)));
        steps.Children.Add(Section(FinishStage.Seal, "5  Seal", Row("Sealer", _sealer)));

        _gSources.Orientation = Orientation.Horizontal;
        var guideBody = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(2, 4, 2, 2),
            Children = { _gTitle, _gHazard, _gAdvice, _gNumbers, _gBasis, _gSources },
        };
        // The guidance sits under the recipe, always in view while open and one click to fold
        // down to its header - available without ever covering anything.
        _guide.Content = new ScrollViewer
        {
            Content = guideBody, MaxHeight = 300,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _guide.Foreground = Ink;
        _guide.Margin = new Thickness(14, 8, 10, 12);
        ToolTip.SetTip(_guide, "What is known about the step you last touched, where it came from, and how sure it is. Fold it away when you know it.");

        var rightDock = new DockPanel();
        DockPanel.SetDock(_guide, Dock.Bottom);
        rightDock.Children.Add(_guide);
        rightDock.Children.Add(new ScrollViewer
        {
            Content = steps, Padding = new Thickness(14, 12, 10, 4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var right = new Border
        {
            Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = rightDock,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,400") };
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var copy = MakeButton("Copy recipe", "Copy the recipe and its checks as text, for your notes or a job sheet.");
        copy.Click += async (_, _) => await CopyRecipeAsync();
        var close = MakeButton("Close", "Close this window. The recipe is remembered until DepthView closes.");
        close.Click += (_, _) => Close();
        var footRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, close } };
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 8) };
        foot.Children.Add(Text("Nothing here changes the depth map. Products are named so you can find them; no maker has endorsed DepthView.", 11, Faint));
        Grid.SetColumn(footRight, 1);
        foot.Children.Add(footRight);
        var footer = new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        return root;
    }

    /// <summary>One step's box. Touching anything in it points the guidance panel at it.</summary>
    private Control Section(FinishStage stage, string heading, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text(heading, 12, Bright, FontWeight.SemiBold));
        foreach (var r in rows) panel.Children.Add(r);
        var box = new Border
        {
            Background = Brush("#1B1F25"), BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = panel, Tag = stage,
        };
        box.GotFocus += (_, _) => ShowStep(stage);
        box.PointerPressed += (_, _) => ShowStep(stage);
        return box;
    }

    private static Control Row(string label, Control c)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*") };
        var t = Text(label, 11, Muted);
        t.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(t);
        Grid.SetColumn(c, 1);
        if (c is ComboBox cb) cb.HorizontalAlignment = HorizontalAlignment.Stretch;
        g.Children.Add(c);
        return g;
    }

    // ------------------------------------------------------------------ choices

    private void Populate()
    {
        Fill(_material, _cat.Materials, _r.Material);
        Fill(_clean, _cat.Cleaning, _r.Clean);
        Fill(_prepolish, _cat.Prepolish, _r.Prepolish);
        FillDarkeners();
        Fill(_tool, _cat.Tools.Where(t => t.Id != "pins"), _r.Tool);
        foreach (var p in _cat.Pressures)
            _pressure.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        _pressure.SelectedIndex = Math.Max(0, _cat.Pressures.FindIndex(p => p.Id == _r.Pressure));
        ToolTip.SetTip(_pressure, "Firm pressure makes a tool cut faster, and pushes a soft tool further down into the relief.");
        Fill(_sealer, _cat.Sealers, _r.Sealer);
        SyncDarkenControls();
        _rubMinutes.Maximum = 30;
        _rubMinutes.Increment = 0.5m;
        _rubMinutes.Value = (decimal)_r.RelieveMinutes;
        SyncChips();
        SyncDepth();

        _material.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _r.Material = Id(_material) ?? "brass";
            FillDarkeners();
            SyncDarkenControls();
            Changed(FinishStage.Raw);
        };
        _clean.SelectionChanged += (_, _) => { if (!_loading) { _r.Clean = Id(_clean)!; Changed(FinishStage.Clean); } };
        _prepolish.SelectionChanged += (_, _) => { if (!_loading) { _r.Prepolish = Id(_prepolish)!; Changed(FinishStage.Polish); } };
        _darkener.SelectionChanged += (_, _) =>
        {
            if (_loading || Id(_darkener) is not { } id) return;
            _r.UseDarkenerDefaults(_cat.Darkener(id));
            SyncDarkenControls();
            Changed(FinishStage.Darken);
        };
        _strength.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _strengthText.Text = StrengthText(_strength.Value);
            if (_loading) return;
            _r.Strength = _strength.Value / 100.0;
            Changed(FinishStage.Darken);
        };
        _darkMinutes.ValueChanged += (_, _) => { if (!_loading) { _r.DarkenMinutes = (double)(_darkMinutes.Value ?? 0); Changed(FinishStage.Darken); } };
        _tool.SelectionChanged += (_, _) => { if (!_loading) { _r.Tool = Id(_tool)!; Changed(FinishStage.Relieve); } };
        _pressure.SelectionChanged += (_, _) => { if (!_loading) { _r.Pressure = Id(_pressure)!; Changed(FinishStage.Relieve); } };
        _rubMinutes.ValueChanged += (_, _) => { if (!_loading) { _r.RelieveMinutes = (double)(_rubMinutes.Value ?? 0); Changed(FinishStage.Relieve); } };
        _sealer.SelectionChanged += (_, _) => { if (!_loading) { _r.Sealer = Id(_sealer)!; Changed(FinishStage.Seal); } };
    }

    private void FillDarkeners()
    {
        bool was = _loading;
        _loading = true;
        var list = _cat.DarkenersFor(_r.Material).ToList();
        if (!list.Any(d => d.Id == _r.Darkener)) _r.UseDarkenerDefaults(list.FirstOrDefault(d => !d.IsNone) ?? list[0]);
        Fill(_darkener, list, _r.Darkener);
        _loading = was;
    }

    private void SyncDarkenControls()
    {
        bool was = _loading;
        _loading = true;
        var d = _cat.Darkener(_r.Darkener);
        _strength.IsEnabled = _darkMinutes.IsEnabled = !d.IsNone;
        _strength.Value = Math.Round(Math.Clamp(_r.Strength, 0.05, 1) * 20) * 5;
        _strengthText.Text = StrengthText(_strength.Value);
        _darkMinutes.Maximum = (decimal)Math.Max(1, d.MaxMinutes);
        _darkMinutes.Increment = d.MaxMinutes > 120 ? 30m : 0.25m;
        _darkMinutes.Value = (decimal)Math.Min(_r.DarkenMinutes, Math.Max(1, d.MaxMinutes));
        SetHazard(_hazard, d.Hazard, compact: true);
        _loading = was;
    }

    private static string StrengthText(double pct) => pct >= 100 ? "neat" : $"{pct:0}%";

    private void Changed(FinishStage step)
    {
        // Changing a later step than the one on show moves the picture on to it: what you just
        // changed is what you want to see.
        if (_r.StopAfter < step) { _r.StopAfter = step; SyncChips(); }
        ShowStep(step);
        Queue();
    }

    private void SyncChips()
    {
        foreach (var b in _chips)
        {
            bool on = (FinishStage)b.Tag! == _r.StopAfter;
            b.Background = on ? Accent : Chip;
            b.Foreground = on ? Brushes.White : Ink;
        }
    }

    private void SyncDepth()
    {
        double stops = _exag.Value;
        _exagText.Text = stops <= 0 ? "true scale" : $"{Math.Pow(2, stops):0.#}x";
        var s = _preview.Settings;
        s.ZStops = stops;
        s.ApparentDepthMm = ZScale.DrawnDepthMm(_depthMm, stops);
        s.BlankMm = _blankMm;
        s.SlabRatio = _thickMm / Math.Max(0.01, _depthMm);
        if (_field is not null) _preview.Request(true);
    }

    // ------------------------------------------------------------------ the work

    private async Task PrepareAsync()
    {
        _status.Text = "Building the tuned map at full resolution...";
        _preview.MaterialOverride = new MaterialPreset { Name = "Finishing", Metallic = true, EnvStrength = 0.6, Ambient = 0.1 };
        _preview.Settings.Orbit = true;
        _preview.Settings.PitchDeg = 62;
        SyncDepth();
        try
        {
            var (field, fw, fh) = await Task.Run(() =>
            {
                var tuned = DepthTuner.Apply(_grey, _w, _h, _maxValue, _full, out var rep);
                var f = ReliefRenderer.BuildHeights(tuned, rep.OutWidth, rep.OutHeight, _maxValue, 900, out int w2, out int h2);
                return (f, w2, h2);
            });
            _field = field; _fw = fw; _fh = fh;
            _preview.SetField(field, fw, fh);
            _loading = false;
            Simulate();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not build the map: " + ex.Message;
        }
    }

    private void Queue()
    {
        if (_loading || _field is null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Simulate()
    {
        if (_field is null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int gen = ++_gen;
        var recipe = _r.Clone();
        var field = _field;
        int fw = _fw, fh = _fh;
        double mmPerPx = _blankMm / Math.Max(1, Math.Min(fw, fh));
        double depth = _depthMm;
        _status.Text = "Working...";

        Task.Run(() =>
        {
            try { return FinishSimulator.Run(_cat, recipe, field, fw, fh, mmPerPx, depth, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || t.Result is not { } res) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _gen) return;
                _last = res;
                _preview.Finish = res.Layer;
                _preview.Request(false);
                ShowChecks(res, recipe);
            });
        }, TaskScheduler.Default);
    }

    private void ShowChecks(FinishResult res, FinishRecipe recipe)
    {
        _checks.Children.Clear();
        foreach (var w in res.Warnings)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
            row.Children.Add(Text("!", 12, Amber, FontWeight.Bold));
            var t = Text(w, 12, Amber);
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            _checks.Children.Add(row);
        }

        var parts = new List<string>();
        if (recipe.StopAfter >= FinishStage.Darken && !_cat.Darkener(recipe.Darkener).IsNone)
        {
            parts.Add($"engraving dark {res.DarkShareEngraved * 100:0}%");
            parts.Add($"deep half dark {res.DarkShareFloors * 100:0}%");
        }
        if (recipe.StopAfter >= FinishStage.Relieve)
            parts.Add($"highs bright {res.BrightShareTop * 100:0}%");
        parts.Add($"{res.Seconds:0.0} s");
        _status.Text = (res.Warnings.Count == 0 ? "No problems found with this recipe.   " : "")
                     + string.Join("   ", parts);
    }

    // ------------------------------------------------------------------ guidance

    private void ShowGeneral()
    {
        _gTitle.Text = "How finishing works";
        _gAdvice.Text = string.Join("\n\n", _cat.General.Select(n => n.Title + ". " + n.Text));
        _gNumbers.Text = "";
        _gBasis.Text = "Touch any step on the right to see what is known about it.";
        _gHazard.IsVisible = false;
        _gSources.Children.Clear();
    }

    private void ShowStep(FinishStage step)
    {
        switch (step)
        {
            case FinishStage.Raw:
            {
                var m = _cat.Material(_r.Material);
                Guide(m, $"Reflectance {m.F0} ({m.F0Basis})   stock roughness {m.StockRoughness:0.00}   laser floor {m.LaserFloorRoughness:0.00} ({m.RoughnessBasis})", null);
                break;
            }
            case FinishStage.Clean:
            {
                var c = _cat.Clean(_r.Clean);
                var bits = new List<string> { $"Laser oxide left {c.OxideLeft * 100:0}%" };
                if (c.BurnishTool is not null) bits.Add($"burnishes for {c.BurnishMinutes:0} min");
                if (c.Pickle > 0) bits.Add($"pink copper film {c.Pickle * 100:0}% on the engraving");
                if (c.AllRoughness is double a) bits.Add($"everything to roughness {a:0.00}");
                Guide(c, string.Join("   ", bits), null);
                break;
            }
            case FinishStage.Polish:
            {
                var p = _cat.Pre(_r.Prepolish);
                Guide(p, p.Tool is { } t ? ToolNumbers(_cat.Tool(t)) + $"   {p.Minutes:0.#} min" : "", null);
                break;
            }
            case FinishStage.Darken:
            {
                var d = _cat.Darkener(_r.Darkener);
                string nums = d.IsNone ? "" :
                    $"About two-thirds done in {FinishRecipe.Minutes(d.TauMinutes)} neat   "
                    + $"starts here at {d.DefaultStrength * 100:0}%, {FinishRecipe.Minutes(d.DefaultMinutes)}"
                    + (d.Overdo < 50 ? "   goes crusty if left long at full strength" : "");
                Guide(d, nums, d.Hazard, d.IsNone ? null : d.Maker);
                break;
            }
            case FinishStage.Relieve:
            {
                var t = _cat.Tool(_r.Tool);
                Guide(t, t.IsNone ? "" : ToolNumbers(t), null);
                break;
            }
            case FinishStage.Seal:
            {
                var s = _cat.Sealer(_r.Sealer);
                string nums = s.Clear > 0 ? $"Clear coat, roughness {s.ClearRoughness:0.00}"
                            : s.Lighten > 0 ? $"Takes the patina back about {s.Lighten * 100:0}%, smooths rough areas by {-s.RoughDelta:0.0#}" : "";
                Guide(s, nums, null);
                break;
            }
        }
    }

    private static string ToolNumbers(FinishTool t)
    {
        string probe = t.RadiusMm is double r ? $"Probe {r * 2:0.##} mm across" : "A plane";
        return $"{probe}   reaches {t.ReachMm:0.0##} mm below the tops   cut {t.Cut:0.0#}   leaves roughness {t.BareRoughness:0.00}"
             + (t.Brushed ? "   directional" : "");
    }

    private void Guide(FinishEntry e, string numbers, FinishHazard? hazard, string? maker = null)
    {
        _gTitle.Text = maker is { Length: > 0 } ? $"{e.Name}  ({maker})" : e.Name;
        _gAdvice.Text = e.Advice;
        _gNumbers.Text = numbers;
        _gNumbers.IsVisible = numbers.Length > 0;
        _gBasis.Text = e.Basis is { Length: > 1 } b ? "Basis: " + b + "   (V = from a source, E = an estimate)" : "";
        SetHazard(_gHazard, hazard, compact: false);

        _gSources.Children.Clear();
        foreach (var s in e.Sources)
        {
            var link = new Button
            {
                Content = s.Title, Padding = new Thickness(0, 1), Margin = new Thickness(0, 0, 12, 2),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Brush("#7FB2E5"), FontSize = 11, Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(link, s.Url);
            string url = s.Url;
            link.Click += async (_, _) =>
            {
                try { await (GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)) ?? Task.FromResult(false)); }
                catch { _status.Text = "Could not open a browser: " + url; }
            };
            _gSources.Children.Add(link);
        }
    }

    private static void SetHazard(Border b, FinishHazard? hz, bool compact)
    {
        if (hz is null) { b.IsVisible = false; return; }
        var (bg, fg) = hz.Level switch
        {
            "danger" => ("#4A1F1A", "#FFB4A8"),
            "warning" => ("#4A3812", "#FFD08A"),
            _ => ("#2C333D", "#C9D3E0"),
        };
        b.Background = Brush(bg);
        b.Child = compact
            ? Text(hz.Badge, 11, Brush(fg), FontWeight.SemiBold)
            : new StackPanel
            {
                Spacing = 2,
                Children = { Text(hz.Badge, 11, Brush(fg), FontWeight.SemiBold), Text(hz.Text, 11, Brush(fg)) },
            };
        if (compact) ToolTip.SetTip(b, hz.Text);
        b.IsVisible = true;
    }

    // ------------------------------------------------------------------ output

    private async Task SavePictureAsync()
    {
        var top = GetTopLevel(this);
        if (top?.StorageProvider is null || _field is null) return;
        if (await _preview.RenderStillAsync(1800) is not { } buf) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save finishing preview",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_fileName) + "-finish-" + _r.StopAfter.ToString().ToLowerInvariant() + ".png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } },
        });
        if (file is null) return;
        try
        {
            await using var s = await file.OpenWriteAsync();
            if (s.CanSeek) s.SetLength(0);
            using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(buf.Pixels, buf.W, buf.H);
            await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(img, s);
            _status.Text = $"Saved {file.Name}.";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save: " + ex.Message;
        }
    }

    private async Task CopyRecipeAsync()
    {
        var clip = GetTopLevel(this)?.Clipboard;
        if (clip is null) return;
        var lines = new List<string> { $"Finish for {_fileName}", _r.Describe(_cat) };
        if (_last is { Warnings.Count: > 0 } res)
        {
            lines.Add("");
            lines.Add("Checks");
            lines.AddRange(res.Warnings.Select(w => "  " + w));
        }
        var d = _cat.Darkener(_r.Darkener);
        if (d.Hazard is { } hz && _r.StopAfter >= FinishStage.Darken)
        {
            lines.Add("");
            lines.Add($"Safety ({hz.Badge}): {hz.Text}");
        }
        await clip.SetTextAsync(string.Join(Environment.NewLine, lines));
        _status.Text = "Recipe copied to the clipboard.";
    }

    // ------------------------------------------------------------------ helpers

    private static void Fill<T>(ComboBox box, IEnumerable<T> items, string selectedId) where T : FinishEntry
    {
        box.Items.Clear();
        int sel = 0, i = 0;
        foreach (var e in items)
        {
            var item = new ComboBoxItem { Content = e.Name, Tag = e.Id };
            if (e.Advice.Length > 0) ToolTip.SetTip(item, e.Advice);
            box.Items.Add(item);
            if (string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase)) sel = i;
            i++;
        }
        box.SelectedIndex = sel;
    }

    private static string? Id(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static NumericUpDown Minutes() => new()
    {
        Minimum = 0, Maximum = 30, Increment = 0.25m, FormatString = "0.##",
        HorizontalAlignment = HorizontalAlignment.Left, Width = 130,
    };

    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Button MakeButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 6),
            Background = Brush("#22262D"), Foreground = Brush("#DDE3EA"), BorderBrush = Brush("#333944"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }
}
