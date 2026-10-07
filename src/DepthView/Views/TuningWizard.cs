using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DepthView.Controls;
using DepthView.Processing;
using DepthView.Rendering;

namespace DepthView.Views;

/// <summary>The image the wizard works on, handed over by the Tune window.</summary>
public sealed record WizardSource(ushort[] Grey, int Width, int Height, int MaxValue,
                                  ushort[] PreviewGrey, int PreviewWidth, int PreviewHeight, double PreviewScale,
                                  long[] Histogram, string FileName);

/// <summary>
/// The tuning wizard: a few questions about intent, each one asked beside the measurement that
/// makes it answerable and the part of the picture it is about.
///
/// It decides nothing on its own. Every question has a recommendation, and every
/// recommendation says what it was read from; the user's answer is what counts. Nor does it
/// tune anything itself: its answers become one <see cref="TuningOptions"/>
/// (<see cref="WizardPlan"/>), the preview is <see cref="DepthTuner.Apply"/> on the Tune
/// window's own reduced copy, and the result is handed back to the Tune window as settings on
/// its controls - one pipeline, so what the wizard shows is what the file will be.
///
/// Built in code, like <see cref="ReleaseNotesWindow"/>: its panel is rebuilt for every step.
/// </summary>
public sealed class TuningWizard : Window
{
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static readonly IBrush Ink = B("#DDE3EA"), InkStrong = B("#EDF1F6"), Muted = B("#8A929E"),
        Faint = B("#5E6773"), Accent = B("#7FA6D8"), Good = B("#8BD49C"), Warn = B("#F0B45C"),
        CardBg = B("#171A1F"), CardLine = B("#2A303A"), CardSel = B("#17222F"), CardSelLine = B("#3E7CB4"),
        PanelBg = B("#14171B"), WindowBg = B("#101215"), WellBg = B("#0C0E11"), Line = B("#262B33");

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    // Overlay colours. Nothing in a depth map is ever coloured, so none of these can be
    // mistaken for data - the same reason the Tune window draws its rings in cyan and amber.
    private static readonly Color CBackground = Color.Parse("#3D7BFF"), CRim = Color.Parse("#38C8FF"),
        CDeep = Color.Parse("#FF5A5A"), CHigh = Color.Parse("#F0B45C"), CFlatten = Color.Parse("#4CD07D"),
        CSmooth = Color.Parse("#4FC3D9"), CLeave = Color.Parse("#A0A8B4"), CDesign = Color.Parse("#8BD49C"),
        CBlank = Color.Parse("#38C8FF"), CRimInner = Color.Parse("#F0B45C");

    private enum StepId { Target, Background, Rim, Placement, Edge, Floor, Top, Flat, Layers, Output, Review }

    private sealed record Step(StepId Id, string Title, string Hint, Func<bool> Applies);

    private sealed record Opt<T>(T Value, string Title, string Detail, string? Pill = null, bool Enabled = true);

    private readonly WizardSource _src;
    private readonly DesignSurvey _s;
    private readonly WizardAnswers _a;
    private readonly List<Step> _steps;
    private int _index;
    private readonly HashSet<int> _visited = new();
    private bool _floorTouched, _topTouched, _flatTouched, _passesTouched;
    private int _focusArea = -1;
    private double _lastRamp;
    private readonly WizardTarget? _rememberedTarget;

    private readonly StackPanel _rail = new() { Spacing = 2 };
    private readonly ContentControl _body = new();
    private readonly ScrollViewer _bodyScroll;
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly ReliefPreview _relief3d = new() { IsVisible = false };
    private readonly ReliefViewSettings _reliefSettings = new();
    private readonly WrapPanel _legend = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _previewTitle = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = B("#9AA3AF"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _previewCaption = new() { FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _showResult = new() { Content = "Show the result", FontSize = 12, Foreground = B("#B7BEC8") };
    private readonly CheckBox _show3d = new() { Content = "Lit 3D", FontSize = 12, Foreground = B("#B7BEC8") };
    private readonly ProfilePlot _profile = new() { Height = 130, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 3, Minimum = 0, Maximum = 1, Foreground = B("#3E7CB4"), Background = B("#1E232A") };
    private readonly Button _backButton, _nextButton;
    private readonly TextBlock _stepCount = new() { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Action> _live = new();
    private readonly DispatcherTimer _debounce;
    private bool _syncing;

    private TuningReport? _rep;
    private TuningOptions? _previewOptions;
    private ushort[]? _tuned;
    private int _tw, _th;

    /// <summary>The settings, when the user pressed Apply; null when the wizard was cancelled.</summary>
    public WizardPlan? Plan { get; private set; }

    /// <summary>The user asked for the save dialog straight after applying.</summary>
    public bool SaveAfter { get; private set; }

    private int Max => _src.MaxValue;
    private double Scale => _src.PreviewScale;
    private StepId Current => _steps[_index].Id;
    private bool BgIsDesign => _a.Background == BackgroundRole.Floor;
    private bool RimCovered => _a.Rim == RimChoice.Replace && _s.DrawnRim is not null;
    private static double BlankMm => Blank.Current.DiameterMm;
    private static double DepthMm => Blank.Current.TargetDepthMm;
    private double CanvasPxPerMm => Math.Min(_src.Width, _src.Height) / BlankMm;

    public TuningWizard(WizardSource src)
    {
        _src = src;
        Title = "Tuning wizard";
        Width = 1280; Height = 860; MinWidth = 980; MinHeight = 640;
        Program.SizeForCapture(this);
        Background = WindowBg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DepthView/Assets/depthview-icon-256.png"))); }
        catch { /* the icon is cosmetic */ }

        // Every question is answered from these measurements, so they are taken once, on the
        // full image, before the first one is asked. A 12-megapixel map takes a tenth of a second.
        _s = DesignSurvey.Run(src.Grey, src.Width, src.Height, src.MaxValue);
        _rememberedTarget = Preferences.Current.WizardLast?.Target;
        _a = InitialAnswers();
        _lastRamp = _a.RampMm > 0 ? _a.RampMm : WizardAdvice.RampMm(_a.Target);

        _steps = new List<Step>
        {
            new(StepId.Target, "Machine and blank", "What cuts it, on what", () => true),
            new(StepId.Background, "Background", "Surround or floor", () => _s.HasDesign && _s.BackgroundShare >= 0.01),
            new(StepId.Rim, "Rim", "The coin's edge", () => true),
            new(StepId.Placement, "Placement", "Where the blank sits", () => _a.Rim == RimChoice.Add),
            new(StepId.Edge, "Edge into the rim", "Step or taper", () => _a.Rim != RimChoice.None),
            new(StepId.Floor, "Deepest areas", "What becomes full depth", () => true),
            new(StepId.Top, "Highest areas", "What stays untouched", () => true),
            new(StepId.Flat, "Flat areas", "Level things engrave level", () => _s.FlatAreas.Count > 0),
            new(StepId.Layers, "Layers and depth", "Keeping Z and focus together", () => true),
            new(StepId.Output, "Output", "The file to write", () => true),
            new(StepId.Review, "Review", "Every change, and why", () => true),
        };

        // The margin is on the content, not the scroller: a ScrollViewer's padding is not taken
        // off the width its content is measured at, so text ran off the right-hand edge.
        _body.Margin = new Thickness(22, 18, 24, 22);
        _bodyScroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _backButton = MakeButton("Back", primary: false);
        _backButton.Click += (_, _) => Move(-1);
        _nextButton = MakeButton("Next", primary: true);
        _nextButton.Click += (_, _) =>
        {
            if (Current == StepId.Review) Finish(save: false);
            else Move(+1);
        };
        var cancel = MakeButton("Cancel", primary: false);
        cancel.Click += (_, _) => Close();
        ToolTip.SetTip(cancel, "Close the wizard and change nothing.");

        _reliefSettings.Orbit = true;
        _reliefSettings.PitchDeg = 55;
        _relief3d.Settings = _reliefSettings;

        _showResult.IsCheckedChanged += (_, _) => { if (!_syncing) Queue(); };
        _show3d.IsCheckedChanged += (_, _) => { if (!_syncing) Queue(); };
        ToolTip.SetTip(_showResult, "Switch between the original map with this step's highlight, and the result of every answer so far.");
        ToolTip.SetTip(_show3d, "Draw the picture as lit metal, 4 times deeper than it will be so the relief can be seen. Drag to turn it.");

        Content = BuildLayout(cancel);

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };

        Blank.Current.Changed += OnBlankChanged;
        Closed += (_, _) => Blank.Current.Changed -= OnBlankChanged;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        // --wizard-step: open at a step, for screenshots. Counted over the steps that apply to
        // this map, which are the ones the rail numbers.
        int start = 0;
        if (Program.StartupWizardStep is int n && n > 1)
        {
            var applicable = Enumerable.Range(0, _steps.Count).Where(i => _steps[i].Applies()).ToList();
            start = applicable[Math.Clamp(n - 1, 0, applicable.Count - 1)];
            foreach (int i in applicable.Where(i => i < start)) _visited.Add(i);
        }
        Go(start);
    }

    private void OnBlankChanged(object? sender, EventArgs e)
    {
        if (_a.Target == WizardTarget.MakeIt && !_passesTouched)
            _a.Passes = WizardAdvice.Passes(_a.Target, DepthMm);
        Changed();
    }

    private WizardAnswers InitialAnswers()
    {
        var a = new WizardAnswers();
        var mem = Preferences.Current.WizardLast;
        var target = Program.StartupWizardTarget ?? mem?.Target ?? WizardTarget.MakeIt;
        WizardAdvice.ApplyTarget(a, target, DepthMm);
        if (mem is not null)
        {
            a.RimMm = mem.RimMm;
            a.FlatScope = mem.FlatScope;
            if (mem.Target == target)
            {
                a.RampMm = mem.RampMm;
                a.Bits = mem.Bits;
                a.WriteDpi = mem.WriteDpi;
                a.Outline = mem.Outline;
            }
        }
        a.Background = WizardAdvice.Background(_s);
        a.Rim = mem is { WantRim: false } ? RimChoice.None : WizardAdvice.Rim(_s);
        a.Placement = WizardAdvice.Placement(_s, a.Background, BlankMm);
        RecommendLevels(a);
        RecommendFlat(a, scopeToo: mem is null);
        return a;
    }

    /// <summary>The floor and top choices follow the measurements until the user picks one.</summary>
    private void RecommendLevels(WizardAnswers a)
    {
        bool bg = a.Background == BackgroundRole.Floor;
        bool rim = a.Rim == RimChoice.Replace && _s.DrawnRim is not null;
        if (!_floorTouched) a.Floor = WizardAdvice.Level(_s.Floor(bg, rim));
        if (!_topTouched) a.Top = WizardAdvice.Level(_s.Top(bg, rim));
    }

    /// <summary>Each flat area's recommendation depends on the level points and the layer count.</summary>
    private void RecommendFlat(WizardAnswers a, bool scopeToo)
    {
        if (_flatTouched) return;
        var (black, white) = WizardPlan.Levels(_s, a);
        a.FlatModes = _s.FlatAreas.Select(f => WizardAdvice.Flat(f, black, white, a.Passes, Max).Mode).ToArray();
        if (!scopeToo) return;
        bool beyondFloor = _s.FlatAreas.Where((f, k) => !f.TouchesFloor && a.FlatModes[k] != FlatMode.Leave).Any();
        a.FlatScope = beyondFloor ? FlatScope.All : _s.FlatAreas.Any(f => f.TouchesFloor) ? FlatScope.Floor : FlatScope.All;
    }

    private Control BuildLayout(Button cancel)
    {
        // ---- header
        var title = new TextBlock { Text = "Tuning wizard", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = InkStrong };
        var sub = new TextBlock
        {
            Text = $"{_src.FileName}  -  a few questions about what you want the coin to be. Every answer shows on the "
                 + "picture as you make it. The recommendations are suggestions: you have the final word, here and in the "
                 + "Tune window afterwards. Nothing is written until you save, and the original is never modified.",
            FontSize = 11.5, Foreground = B("#79818D"), TextWrapping = TextWrapping.Wrap,
        };
        var header = new Border
        {
            Background = CardBg, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12, 18, 10),
            Child = new StackPanel { Spacing = 3, Children = { title, sub } },
        };

        // ---- step rail
        var railHost = new Border
        {
            Background = PanelBg, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 14, 10, 14),
            Child = new ScrollViewer { Content = _rail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };

        // ---- preview
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _showResult, _show3d } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(_previewTitle);
        Grid.SetColumn(toggles, 1);
        top.Children.Add(toggles);

        var pictures = new Grid();
        pictures.Children.Add(_preview);
        pictures.Children.Add(_relief3d);
        RenderOptions.SetBitmapInterpolationMode(_preview, BitmapInterpolationMode.HighQuality);

        var previewGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto") };
        previewGrid.Children.Add(top);
        Grid.SetRow(pictures, 1); previewGrid.Children.Add(pictures);
        _legend.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_legend, 2); previewGrid.Children.Add(_legend);
        Grid.SetRow(_profile, 3); previewGrid.Children.Add(_profile);
        Grid.SetRow(_previewCaption, 4); previewGrid.Children.Add(_previewCaption);

        var previewHost = new Border
        {
            Margin = new Thickness(14, 14, 14, 14), Padding = new Thickness(12),
            Background = WellBg, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = previewGrid,
        };

        // ---- question panel
        var bodyHost = new Border
        {
            Background = PanelBg, BorderBrush = Line, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = _bodyScroll,
        };

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,460") };
        main.Children.Add(railHost);
        Grid.SetColumn(previewHost, 1); main.Children.Add(previewHost);
        Grid.SetColumn(bodyHost, 2); main.Children.Add(bodyHost);

        // ---- footer
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _backButton, _nextButton } };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { cancel, _stepCount } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 10) };
        footer.Children.Add(left);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        var footerHost = new Border
        {
            Background = CardBg, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel { Children = { _progress, footer } },
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footerHost, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footerHost);
        root.Children.Add(main);
        return root;
    }

    // ------------------------------------------------------------------ navigation

    private void Go(int index)
    {
        _index = index;
        _visited.Add(index);
        _focusArea = -1;

        // Recommendations that depend on earlier answers are refreshed on the way in, unless
        // the user has already made that choice themselves.
        switch (Current)
        {
            case StepId.Floor or StepId.Top: RecommendLevels(_a); break;
            case StepId.Flat: RecommendFlat(_a, scopeToo: false); break;
        }

        _live.Clear();
        _liveAfterRender.Clear();
        _body.Content = BuildStep(Current);
        _bodyScroll.Offset = new Vector(0, 0);

        // Each step opens on the view that shows its question best. The toggles stay the
        // user's to flip.
        _syncing = true;
        _showResult.IsChecked = Current is StepId.Placement or StepId.Edge or StepId.Layers or StepId.Output or StepId.Review;
        _show3d.IsChecked = Current is StepId.Layers or StepId.Review;
        _syncing = false;

        UpdateRail();
        UpdateFooter();
        Changed();
    }

    private void Move(int direction)
    {
        int i = _index + direction;
        while (i >= 0 && i < _steps.Count && !_steps[i].Applies()) i += direction;
        if (i >= 0 && i < _steps.Count) Go(i);
    }

    private List<int> ApplicableSteps() => Enumerable.Range(0, _steps.Count).Where(i => _steps[i].Applies()).ToList();

    private void UpdateFooter()
    {
        var steps = ApplicableSteps();
        int pos = steps.IndexOf(_index) + 1;
        _stepCount.Text = $"Step {pos} of {steps.Count}";
        _progress.Value = steps.Count <= 1 ? 1 : (pos - 1) / (double)(steps.Count - 1);
        _backButton.IsEnabled = pos > 1;
        int next = steps.FirstOrDefault(i => i > _index, -1);
        _nextButton.Content = Current == StepId.Review ? "Apply to the Tune window"
                            : next >= 0 && _steps[next].Id == StepId.Review ? "Review" : "Next";
    }

    private void UpdateRail()
    {
        _rail.Children.Clear();
        _rail.Children.Add(new TextBlock
        {
            Text = "STEPS", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Faint,
            Margin = new Thickness(8, 0, 0, 8), LetterSpacing = 1.2,
        });

        int number = 0;
        for (int i = 0; i < _steps.Count; i++)
        {
            var st = _steps[i];
            bool applies = st.Applies();
            if (applies) number++;
            bool current = i == _index, done = _visited.Contains(i) && !current && applies;

            var badge = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                Background = current ? CardSelLine : done ? B("#1F3A2A") : B("#1E232A"),
                BorderBrush = current ? B("#5D9BD3") : done ? B("#2F6B45") : B("#333944"),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = !applies ? "-" : done ? "✓" : number.ToString(CultureInfo.CurrentCulture),
                    FontSize = 11, FontWeight = FontWeight.SemiBold,
                    Foreground = current ? InkStrong : done ? Good : Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var text = new StackPanel
            {
                Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = st.Title, FontSize = 12.5, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
                                    Foreground = !applies ? Faint : current ? InkStrong : done ? Ink : Muted },
                    new TextBlock { Text = applies ? st.Hint : "Not needed for this map", FontSize = 10.5, Foreground = Faint,
                                    TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(badge);
            Grid.SetColumn(text, 1);
            text.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(text);

            var item = new Border
            {
                Padding = new Thickness(8, 7), CornerRadius = new CornerRadius(6),
                Background = current ? CardSel : Brushes.Transparent,
                Child = row,
            };
            // Any step already seen can be returned to directly; the ones ahead are reached in
            // order, so nothing is skipped by accident.
            int target = i;
            if (applies && _visited.Contains(i) && !current)
            {
                item.Cursor = new Cursor(StandardCursorType.Hand);
                item.PointerPressed += (_, _) => Go(target);
                ToolTip.SetTip(item, "Go back to this step");
            }
            _rail.Children.Add(item);
        }
    }

    /// <summary>An answer changed: refresh every live figure on the panel, then the picture.</summary>
    private void Changed()
    {
        _syncing = true;
        try { foreach (var a in _live.ToArray()) a(); }
        finally { _syncing = false; }
        // An answer can make a later step apply or not (no rim, no placement question).
        UpdateRail();
        UpdateFooter();
        Queue();
    }

    private void Queue()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    // ------------------------------------------------------------------ the picture

    private WizardPlan BuildPlan() => WizardPlan.Build(_s, _a, _src.FileName, BlankMm, DepthMm);

    /// <summary>
    /// The plan on the Tune window's reduced copy - the same arithmetic the Tune window runs on
    /// every tick, with only the rim, which is in pixels, scaled to the smaller canvas.
    /// </summary>
    private void RenderPreview()
    {
        var po = BuildPlan().Options;
        po.RimRadius *= Scale;
        po.RimRamp *= Scale;
        _tuned = DepthTuner.Apply(_src.PreviewGrey, _src.PreviewWidth, _src.PreviewHeight, Max, po, out var rep);
        _rep = rep;
        _previewOptions = po;
        _tw = rep.OutWidth;
        _th = rep.OutHeight;

        bool result = _showResult.IsChecked == true;
        bool lit = _show3d.IsChecked == true;
        var id = Current;

        _preview.IsVisible = !lit;
        _relief3d.IsVisible = lit;
        if (lit)
        {
            var g = result ? _tuned : _src.PreviewGrey;
            int w = result ? _tw : _src.PreviewWidth, h = result ? _th : _src.PreviewHeight;
            _reliefSettings.BlankMm = BlankMm;
            _reliefSettings.ZStops = 2;
            _reliefSettings.ApparentDepthMm = ZScale.DrawnDepthMm(DepthMm, 2);
            _reliefSettings.SlabRatio = Blank.Current.ThicknessMm / Math.Max(0.01, DepthMm);
            // Terraces only where the job really is a stack of slices.
            _reliefSettings.SliceCount = id == StepId.Layers && _a.Target != WizardTarget.LightBurn ? _a.Passes : 0;
            var field = ReliefRenderer.BuildHeights(g, w, h, Max, 380, out int fw, out int fh);
            _relief3d.SetField(field, fw, fh);
        }
        else
        {
            _preview.Source = result
                ? Paint(_tuned, _tw, _th, null, ResultRings(po))
                : Paint(_src.PreviewGrey, _src.PreviewWidth, _src.PreviewHeight, SourceOverlay(id), SourceRings(id));
        }

        _previewTitle.Text = (result ? (lit ? "Result" : "Result of your answers so far") : "Original")
                           + (lit ? ", lit, 4x deep" : "")
                           + (lit && _reliefSettings.SliceCount > 0 ? $", {_a.Passes:N0} layers" : "");
        FillLegend(id, result);
        _previewCaption.Text = Caption(id, result, rep);

        bool profile = id == StepId.Edge && _a.Rim != RimChoice.None;
        _profile.IsVisible = profile;
        if (profile) FillProfile(po);

        foreach (var a in _liveAfterRender.ToArray()) a();
    }

    /// <summary>Figures that come from the rendered preview (fit, rim overlap), refreshed after each render.</summary>
    private readonly List<Action> _liveAfterRender = new();

    private (int Black, int White) Levels => WizardPlan.Levels(_s, _a);

    private sealed record Ring(double Cx, double Cy, double R, Color C);

    private List<Ring> ResultRings(TuningOptions po)
    {
        var rings = new List<Ring>();
        if (!po.AddRim) return rings;
        double cx = (_tw - 1) / 2.0, cy = (_th - 1) / 2.0;
        rings.Add(new Ring(cx, cy, Math.Min(_tw, _th) / 2.0, CBlank));
        if (po.RimRadius > 0) rings.Add(new Ring(cx, cy, po.RimRadius, CRimInner));
        return rings;
    }

    private List<Ring> SourceRings(StepId id)
    {
        var rings = new List<Ring>();
        if (!_s.HasDesign) return rings;
        double cx = _s.CentreX * Scale, cy = _s.CentreY * Scale;
        if (id == StepId.Target)
        {
            rings.Add(new Ring(cx, cy, _s.Radius * Scale, CDesign));
            rings.Add(new Ring(cx, cy, 2.5, CDesign));
        }
        return rings;
    }

    /// <summary>Does this source pixel count as design under the answers so far?</summary>
    private bool InReading(int x, int y, int v)
    {
        if (!BgIsDesign && IsBackground(x, y)) return false;
        if (RimCovered && _s.DrawnRim is { } rim)
        {
            double dx = x - _s.CentreX * Scale, dy = y - _s.CentreY * Scale, lim = rim.Inner * 0.98 * Scale;
            if (dx * dx + dy * dy > lim * lim) return false;
        }
        return true;
    }

    /// <summary>
    /// Is this preview pixel background, as the survey found it - one level, or a shaded
    /// surround followed from the edge? The preview is a nearest-neighbour reduction, so each
    /// of its pixels is one source pixel, found the same way.
    /// </summary>
    private bool IsBackground(int px, int py)
        => _s.IsSurround((int)((long)px * _src.Width / _src.PreviewWidth), (int)((long)py * _src.Height / _src.PreviewHeight));

    private int[]? _areaAt;

    /// <summary>Which flat area each preview pixel belongs to, or -1. Worked out once.</summary>
    private int[] AreaAt()
    {
        if (_areaAt is not null) return _areaAt;
        int pw = _src.PreviewWidth, ph = _src.PreviewHeight;
        var at = new int[pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int found = -1;
                for (int k = 0; k < _s.FlatAreas.Count && found < 0; k++)
                {
                    var f = _s.FlatAreas[k];
                    if (f.Mask[(int)((long)y * f.MaskH / ph) * f.MaskW + (int)((long)x * f.MaskW / pw)]) found = k;
                }
                at[y * pw + x] = found;
            }
        return _areaAt = at;
    }

    private delegate (Color C, double Alpha)? Overlay(int x, int y, int v);

    /// <summary>The highlight for a step: the part of the picture its question is about.</summary>
    private Overlay? SourceOverlay(StepId id)
    {
        var (black, white) = Levels;
        switch (id)
        {
            case StepId.Background:
                return (x, y, v) => IsBackground(x, y) ? (CBackground, 0.62) : (Colors.Black, 0.35);

            case StepId.Rim when _s.DrawnRim is { } rim:
            {
                double cx = _s.CentreX * Scale, cy = _s.CentreY * Scale;
                double r0 = rim.Inner * Scale, r1 = rim.Outer * Scale;
                return (x, y, v) =>
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    return d >= r0 && d <= r1 ? (CRim, 0.7) : (Colors.Black, 0.3);
                };
            }

            case StepId.Floor:
                return (x, y, v) => !InReading(x, y, v) ? (Colors.Black, 0.75)
                                  : v <= black ? (CDeep, 0.85) : (Colors.Black, 0.40);

            case StepId.Top:
                return (x, y, v) => !InReading(x, y, v) ? (Colors.Black, 0.75)
                                  : v >= white ? (CHigh, 0.85) : (Colors.Black, 0.40);

            case StepId.Flat:
            {
                var at = AreaAt();
                int pw = _src.PreviewWidth;
                return (x, y, v) =>
                {
                    int k = at[y * pw + x];
                    if (k < 0) return (Colors.Black, 0.45);
                    if (_focusArea >= 0 && k != _focusArea) return (CLeave, 0.12);
                    bool inScope = WizardPlan.InScope(_s.FlatAreas[k], _a.FlatScope);
                    var mode = inScope && k < _a.FlatModes.Length ? _a.FlatModes[k] : FlatMode.Leave;
                    return mode switch
                    {
                        FlatMode.Flatten => (CFlatten, 0.72),
                        FlatMode.Smooth => (CSmooth, 0.72),
                        _ => (CLeave, inScope ? 0.45 : 0.22),
                    };
                };
            }

            default:
                return null;
        }
    }

    /// <summary>Grey to BGRA, with an overlay and rings drawn in colours no depth map contains.</summary>
    private WriteableBitmap Paint(ushort[] grey, int w, int h, Overlay? overlay, List<Ring> rings)
    {
        var buf = new byte[(long)w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                long i = (long)y * w + x;
                int v = grey[i];
                double g = (double)v * 255 / Math.Max(1, Max);
                double r = g, gg = g, b = g;
                if (overlay?.Invoke(x, y, v) is { } o)
                {
                    r = r * (1 - o.Alpha) + o.C.R * o.Alpha;
                    gg = gg * (1 - o.Alpha) + o.C.G * o.Alpha;
                    b = b * (1 - o.Alpha) + o.C.B * o.Alpha;
                }
                long d = i * 4;
                buf[d] = (byte)Math.Clamp(b, 0, 255);
                buf[d + 1] = (byte)Math.Clamp(gg, 0, 255);
                buf[d + 2] = (byte)Math.Clamp(r, 0, 255);
                buf[d + 3] = 255;
            }

        foreach (var ring in rings) DrawRing(buf, w, h, ring);

        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(buf, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }
        return bmp;
    }

    /// <summary>A circle two pixels wide, scanned by rows and by columns so it has no gaps.</summary>
    private static void DrawRing(byte[] buf, int w, int h, Ring ring)
    {
        void Put(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            long d = ((long)y * w + x) * 4;
            buf[d] = ring.C.B; buf[d + 1] = ring.C.G; buf[d + 2] = ring.C.R;
        }
        for (double rr = ring.R - 0.5; rr <= ring.R + 0.5; rr += 1)
        {
            if (rr <= 0) continue;
            for (int y = (int)(ring.Cy - rr) - 1; y <= (int)(ring.Cy + rr) + 1; y++)
            {
                double inside = rr * rr - (y - ring.Cy) * (y - ring.Cy);
                if (inside < 0) continue;
                double dx = Math.Sqrt(inside);
                Put((int)Math.Round(ring.Cx - dx), y);
                Put((int)Math.Round(ring.Cx + dx), y);
            }
            for (int x = (int)(ring.Cx - rr) - 1; x <= (int)(ring.Cx + rr) + 1; x++)
            {
                double inside = rr * rr - (x - ring.Cx) * (x - ring.Cx);
                if (inside < 0) continue;
                double dy = Math.Sqrt(inside);
                Put(x, (int)Math.Round(ring.Cy - dy));
                Put(x, (int)Math.Round(ring.Cy + dy));
            }
        }
    }

    private void FillLegend(StepId id, bool result)
    {
        _legend.Children.Clear();
        if (_show3d.IsChecked == true) return;

        var items = new List<(Color C, string Text)>();
        if (result)
        {
            if (_previewOptions?.AddRim == true)
            {
                items.Add((CBlank, "Edge of the blank"));
                items.Add((CRimInner, "Where engraving stops"));
            }
        }
        else switch (id)
        {
            case StepId.Target: items.Add((CDesign, "The design, as measured")); break;
            case StepId.Background: items.Add((CBackground, "Background")); break;
            case StepId.Rim when _s.DrawnRim is not null: items.Add((CRim, "Rim drawn in the artwork")); break;
            case StepId.Floor: items.Add((CDeep, "Becomes full depth")); items.Add((Color.Parse("#2A2D33"), "Not design, under your answers")); break;
            case StepId.Top: items.Add((CHigh, "Stays untouched")); items.Add((Color.Parse("#2A2D33"), "Not design, under your answers")); break;
            case StepId.Flat:
                items.Add((CFlatten, "Flattened"));
                items.Add((CSmooth, "Smoothed"));
                items.Add((CLeave, "Left as drawn"));
                break;
        }

        foreach (var (c, text) in items)
            _legend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 16, 2),
                Children =
                {
                    new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c),
                                 VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 11.5, Foreground = B("#B7BEC8"), VerticalAlignment = VerticalAlignment.Center },
                },
            });
    }

    private string Caption(StepId id, bool result, TuningReport rep)
    {
        if (result)
        {
            var parts = new List<string>();
            if (rep.Fit is { } fit)
                parts.Add($"The design spans {fit.ArtAcrossMm:F1} of the {BlankMm:0.#} mm blank.");
            if (_previewOptions?.AddRim == true)
                parts.Add(rep.RimClipped > 0
                    ? $"The rim covers {rep.RimClippedFraction * 100:F2}% of the design."
                    : "The rim sits clear of the design.");
            if (rep.FitDesignLost > 0)
                parts.Add("Centring was refused: it would have cropped part of the design.");
            if (_show3d.IsChecked == true && id == StepId.Layers && _a.Target == WizardTarget.LightBurn)
                parts.Add("Grayscale mode does not cut in slices, so no terraces are drawn.");
            return parts.Count > 0 ? string.Join("  ", parts) : "The result of every answer so far.";
        }

        var (black, white) = Levels;
        switch (id)
        {
            case StepId.Target when _s.HasDesign:
                return $"The design is {2 * _s.Radius / CanvasPxPerMm:F1} mm across at this blank, and its centre is "
                     + $"{WizardAdvice.OffCentreMm(_s, BlankMm):F1} mm from the middle of the image.";
            case StepId.Background:
                return (_s.SurroundShaded
                         ? $"Levels {_s.BackgroundLow:N0} to {_s.BackgroundHigh:N0}, followed in from the edge of the image: "
                         : $"Level {_s.Background:N0}, around the edge of the image: ")
                     + $"{_s.BackgroundShare * 100:F1}% of the image, "
                     + $"and {_s.BackgroundInsideShare * 100:F1}% of the area inside the design's circle.";
            case StepId.Rim:
                return _s.DrawnRim is { } rim
                    ? $"The artwork's own raised rim, {DrawnRimMm(rim):F2} mm wide once the blank is sized to it."
                    : "No raised rim was found in the artwork.";
            case StepId.Floor:
            {
                var (n, total) = _s.CountIn(0, black, BgIsDesign, RimCovered);
                return $"{Pct(n, total)} of the design becomes full depth (everything at or below level {black:N0}).";
            }
            case StepId.Top:
            {
                var (n, total) = _s.CountIn(white, Max, BgIsDesign, RimCovered);
                return $"{Pct(n, total)} of the design stays untouched (everything at or above level {white:N0}).";
            }
            case StepId.Flat:
                return "Click an area on the right to see it alone.";
            default:
                return "The original map, as it is in the file.";
        }
    }

    private static string Pct(long n, long total)
    {
        double p = total > 0 ? n * 100.0 / total : 0;
        return p >= 10 ? $"{p:F0}%" : p >= 1 ? $"{p:F1}%" : p > 0 ? $"{p:F2}%" : "None";
    }

    /// <summary>The drawn rim's width in millimetres once the blank is sized so that rim sits under ours.</summary>
    private double DrawnRimMm(DepthCanvas.DesignRim rim) => rim.Width / (rim.Outer / (BlankMm / 2));

    /// <summary>The average cross-section near the edge of the blank, from the result.</summary>
    private void FillProfile(TuningOptions po)
    {
        if (_tuned is null) return;
        int w = _tw, h = _th;
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, R = Math.Min(w, h) / 2.0;
        double ppmm = Math.Min(w, h) / BlankMm;
        double spanMm = Math.Min(BlankMm / 2, Math.Max(3.0, _a.RimMm + _a.RampMm + 2.0));
        double r0 = Math.Max(0, R - spanMm * ppmm);
        int bins = Math.Max(2, (int)Math.Ceiling(R - r0));
        var sum = new double[bins];
        var count = new int[bins];
        for (int y = 0; y < h; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + dy * dy);
                if (d < r0 || d >= R) continue;
                int b = Math.Min(bins - 1, (int)(d - r0));
                sum[b] += _tuned[(long)y * w + x];
                count[b]++;
            }
        }
        var depth = new double[bins];
        for (int b = 0; b < bins; b++)
            depth[b] = count[b] > 0 ? 1 - sum[b] / count[b] / Max : (b > 0 ? depth[b - 1] : 0);

        _profile.SetData(depth, r0 / ppmm, R / ppmm,
                         po.AddRim ? po.RimRadius / ppmm : double.NaN,
                         po.AddRim && po.RimRamp > 0 ? (po.RimRadius - po.RimRamp) / ppmm : double.NaN);
    }

    // ------------------------------------------------------------------ panel parts

    private static TextBlock Text(string s, double size = 13, IBrush? fg = null, FontWeight weight = FontWeight.Normal)
        => new() { Text = s, FontSize = size, Foreground = fg ?? Ink, TextWrapping = TextWrapping.Wrap, FontWeight = weight, LineHeight = size * 1.42 };

    private TextBlock LiveText(Func<string> text, double size = 12, IBrush? fg = null)
    {
        var t = Text("", size, fg ?? Muted);
        _live.Add(() => t.Text = text());
        return t;
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Accent, Margin = new Thickness(0, 16, 0, 8),
    };

    private Control Header(string title, string question)
    {
        var kicker = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Accent, LetterSpacing = 1.2 };
        void Count()
        {
            var steps = ApplicableSteps();
            kicker.Text = $"STEP {steps.IndexOf(_index) + 1} OF {steps.Count}";
        }
        Count();
        _live.Add(Count);
        return new StackPanel
        {
            Spacing = 5, Margin = new Thickness(0, 0, 0, 16),
            Children =
            {
                kicker,
                new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = InkStrong, TextWrapping = TextWrapping.Wrap },
                Text(question, 14.5, Ink),
            },
        };
    }

    /// <summary>The facts the question is answered from, refreshed whenever an answer changes them.</summary>
    private Control Measured(Func<IEnumerable<(string Label, string Value)>> rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        void Fill()
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            int r = 0;
            foreach (var (label, value) in rows())
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var l = new TextBlock { Text = label, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 2, 14, 2) };
                var v = new TextBlock { Text = value, FontSize = 12, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
                Grid.SetRow(l, r);
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                grid.Children.Add(l);
                grid.Children.Add(v);
                r++;
            }
        }
        Fill();
        _live.Add(Fill);
        return new Border
        {
            Background = B("#0F1317"), BorderBrush = B("#243040"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 16),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "WHAT DEPTHVIEW MEASURED", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Accent, LetterSpacing = 1.1 },
                    grid,
                },
            },
        };
    }

    private static Control Why(string text) => new Border
    {
        BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 20, 0, 0),
        Child = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = "WHY IT MATTERS", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Faint, LetterSpacing = 1.1 },
                Text(text, 12.5, Muted),
            },
        },
    };

    private static Control Pill(string text, bool recommended) => new Border
    {
        Background = recommended ? B("#1D3A28") : B("#22324A"),
        CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2),
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 1, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = recommended ? Good : Accent },
    };

    /// <summary>
    /// A set of answer cards. The recommended one carries a pill; picking one changes the
    /// answer and redraws the picture straight away, which is the point - the user sees what
    /// each answer does before moving on.
    /// </summary>
    private Control Choices<T>(Func<T> get, Action<T> set, params Opt<T>[] opts)
    {
        var panel = new StackPanel { Spacing = 8 };
        var cards = new List<(Opt<T> Opt, Border Card, Avalonia.Controls.Shapes.Ellipse Dot, Border Ring)>();
        foreach (var o in opts)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = InkStrong,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var ring = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
                Child = dot, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            titleRow.Children.Add(new TextBlock { Text = o.Title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = InkStrong, TextWrapping = TextWrapping.Wrap });
            if (o.Pill is string p)
            {
                var pill = Pill(p, p == "Recommended");
                Grid.SetColumn(pill, 1);
                titleRow.Children.Add(pill);
            }
            var stack = new StackPanel { Spacing = 3, Children = { titleRow, Text(o.Detail, 12, Muted) } };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            g.Children.Add(ring);
            Grid.SetColumn(stack, 1);
            g.Children.Add(stack);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1.5),
                Child = g, Opacity = o.Enabled ? 1 : 0.45,
            };
            if (o.Enabled)
            {
                card.Cursor = new Cursor(StandardCursorType.Hand);
                var value = o.Value;
                card.PointerPressed += (_, _) => { set(value); Changed(); };
            }
            cards.Add((o, card, dot, ring));
            panel.Children.Add(card);
        }

        void Paint()
        {
            var cur = get();
            foreach (var c in cards)
            {
                bool sel = EqualityComparer<T>.Default.Equals(c.Opt.Value, cur);
                c.Card.Background = sel ? CardSel : CardBg;
                c.Card.BorderBrush = sel ? CardSelLine : CardLine;
                c.Dot.IsVisible = sel;
                c.Ring.BorderBrush = sel ? B("#5D9BD3") : B("#4A5361");
            }
        }
        Paint();
        _live.Add(Paint);
        return panel;
    }

    /// <summary>A number with its unit, and a live line beneath saying what it comes to.</summary>
    private Control Number(string label, decimal min, decimal max, decimal step, string format,
                           Func<double> get, Action<double> set, string unit, Func<string>? note = null, Func<bool>? enabled = null)
    {
        var box = new NumericUpDown
        {
            Minimum = min, Maximum = max, Increment = step, FormatString = format,
            Value = (decimal)get(), Width = 130, FontSize = 12.5,
            Background = WellBg, Foreground = Ink, BorderBrush = B("#333944"),
        };
        box.ValueChanged += (_, e) =>
        {
            if (_syncing || e.NewValue is not decimal v) return;
            set((double)v);
            Changed();
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        var l = Text(label, 12.5, Ink);
        l.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(l);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        var u = new TextBlock { Text = unit, FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Width = 28 };
        Grid.SetColumn(u, 2);
        row.Children.Add(u);

        var panel = new StackPanel { Spacing = 2, Children = { row } };
        if (note is not null) panel.Children.Add(LiveText(note, 11.5, Faint));
        _live.Add(() =>
        {
            decimal v = (decimal)get();
            if (box.Value != v) box.Value = v;
            if (enabled is not null) panel.IsEnabled = enabled();
            panel.Opacity = panel.IsEnabled ? 1 : 0.45;
        });
        return panel;
    }

    private Control Check(string label, string detail, Func<bool> get, Action<bool> set, string? pill = null)
    {
        var box = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 4, 0) };
        box.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            set(box.IsChecked == true);
            Changed();
        };
        _live.Add(() => box.IsChecked = get());
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = InkStrong, TextWrapping = TextWrapping.Wrap });
        if (pill is not null)
        {
            var p = Pill(pill, pill == "Recommended");
            Grid.SetColumn(p, 1);
            title.Children.Add(p);
        }
        var text = new StackPanel { Spacing = 2, Children = { title, Text(detail, 12, Muted) } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 6, 0, 0) };
        g.Children.Add(box);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    /// <summary>
    /// The design's own histogram under the current answers, with the two level points on it.
    /// Dragging a marker is a custom answer; it is the same control the Tune window uses.
    /// </summary>
    private Control Strip()
    {
        var strip = new LevelStripControl { Height = 104, Margin = new Thickness(0, 4, 0, 0) };
        strip.LogScale = true;
        (bool, bool) shownFor = (!BgIsDesign, !RimCovered);   // forces the first fill
        void Sync()
        {
            if (shownFor != (BgIsDesign, RimCovered))
            {
                strip.SetData(_s.ReadingHistogram(BgIsDesign, RimCovered), Max);
                shownFor = (BgIsDesign, RimCovered);
            }
            var (black, white) = Levels;
            strip.SetLevels(black, white);
        }
        Sync();
        _live.Add(Sync);
        strip.LevelsChanged += (_, _) =>
        {
            if (_syncing) return;
            var (black, white) = Levels;
            if (strip.Black != black) { _floorTouched = true; _a.Floor = LevelChoice.Custom; _a.BlackCustom = strip.Black; }
            if (strip.White != white) { _topTouched = true; _a.Top = LevelChoice.Custom; _a.WhiteCustom = strip.White; }
            Changed();
        };
        var caption = Text("Drag the red or amber marker to set a level by hand. The histogram is the design's own, on a log scale.", 11.5, Faint);
        return new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0), Children = { strip, caption } };
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            Padding = primary ? new Thickness(18, 8) : new Thickness(13, 8),
            Background = primary ? B("#2A5E8C") : B("#22262D"),
            Foreground = primary ? B("#EDF3FA") : Ink,
            BorderBrush = primary ? B("#3E7CB4") : B("#333944"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            FontSize = 12.5,
            MinWidth = primary ? 96 : 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        return b;
    }

    // ------------------------------------------------------------------ the steps

    private bool _placementTouched;

    private Control BuildStep(StepId id) => id switch
    {
        StepId.Target => StepTarget(),
        StepId.Background => StepBackground(),
        StepId.Rim => StepRim(),
        StepId.Placement => StepPlacement(),
        StepId.Edge => StepEdge(),
        StepId.Floor => StepLevel(deepest: true),
        StepId.Top => StepLevel(deepest: false),
        StepId.Flat => StepFlat(),
        StepId.Layers => StepLayers(),
        StepId.Output => StepOutput(),
        _ => StepReview(),
    };

    private static List<(string, string)> Rows(params (string, string)[] rows) => rows.ToList();

    private Control StepTarget()
    {
        var p = new StackPanel();
        p.Children.Add(Header("Machine and blank", "What will cut this map, and on what blank?"));
        p.Children.Add(Measured(() => Rows(
            ("Map", $"{_src.Width:N0} x {_src.Height:N0} pixels, levels 0 to {Max:N0}"),
            ("Design", _s.HasDesign
                ? $"{2 * _s.Radius / CanvasPxPerMm:F1} mm across at this blank, {WizardAdvice.OffCentreMm(_s, BlankMm):F1} mm off centre"
                : "nothing found apart from the background"),
            ("Background", _s.BackgroundShare >= 0.01 ? $"level {_s.Background:N0}, {_s.BackgroundShare * 100:F0}% of the image" : "none"),
            ("Drawn rim", _s.DrawnRim is { } r ? $"yes, about {DrawnRimMm(r):F2} mm wide" : "none"),
            ("Flat areas", _s.FlatAreas.Count == 0 ? "none" : $"{_s.FlatAreas.Count} nearly level areas"))));

        string? Last(WizardTarget t) => _rememberedTarget == t ? "Last time" : null;
        p.Children.Add(Section("The program that will cut it"));
        p.Children.Add(Choices(() => _a.Target, t =>
            {
                WizardAdvice.ApplyTarget(_a, t, DepthMm);
                _lastRamp = _a.RampMm;
                _passesTouched = false;
                RecommendFlat(_a, scopeToo: false);
            },
            new Opt<WizardTarget>(WizardTarget.MakeIt, "WeCreat MakeIt",
                "Relief (Emboss) mode. MakeIt works in 8 bits - at most 256 layers - and lowers Z 0.01 mm a layer.", Last(WizardTarget.MakeIt)),
            new Opt<WizardTarget>(WizardTarget.LightBurn, "LightBurn, Grayscale image",
                "An Image layer in Grayscale mode on a G-code machine such as the Lumos Ultra. Power follows the grey and every pass covers the whole image.",
                Last(WizardTarget.LightBurn)),
            new Opt<WizardTarget>(WizardTarget.Slicer, "A slicer: 3D Slice and similar",
                "One band of levels per pass, as LightBurn's 3D Slice does on a galvo machine. DepthView's layer figures apply directly.",
                Last(WizardTarget.Slicer))));

        p.Children.Add(Section("The blank"));
        p.Children.Add(Number("Diameter", (decimal)Blank.MinDiameterMm, (decimal)Blank.MaxDiameterMm, 0.5m, "0.0#",
            () => Blank.Current.DiameterMm, v => Blank.Current.DiameterMm = v, "mm"));
        p.Children.Add(Number("Thickness", (decimal)Blank.MinThicknessMm, (decimal)Blank.MaxThicknessMm, 0.1m, "0.0#",
            () => Blank.Current.ThicknessMm, v => Blank.Current.ThicknessMm = v, "mm"));
        p.Children.Add(Number("Target depth", (decimal)Blank.MinDepthMm, (decimal)Blank.MaxDepthMm, 0.01m, "0.00",
            () => Math.Round(DepthMm, 2), v => Blank.Current.TargetDepthMm = v, "mm",
            () => Blank.Current.DepthWarning ?? Blank.Current.DepthSourceText + " The deepest cut - black - is meant to reach this."));

        p.Children.Add(Why("The program decides how depth is made - layers that each cut a smaller area, or power that follows "
            + "the grey - and that decides the later recommendations: bit depth, how wide a taper must be to survive, how many "
            + "layers. Your answer only sets defaults, and each one is shown again where it matters. The blank's diameter puts "
            + "millimetres on the map; the target depth changes no pixel, but every layer figure is worked out from it."));
        return p;
    }

    private Control StepBackground()
    {
        var rec = WizardAdvice.Background(_s);
        string? R(BackgroundRole r) => r == rec ? "Recommended" : null;
        var p = new StackPanel();
        p.Children.Add(Header("Background", "Is the background part of the coin?"));
        p.Children.Add(Measured(() => Rows(
            ("Level", _s.SurroundShaded
                ? $"not one level: {_s.BackgroundLow:N0} to {_s.BackgroundHigh:N0}, shaded or marked - followed in from the edge of the image"
                : $"{_s.Background:N0}, from around the edge of the image"),
            ("Covers", $"{_s.BackgroundShare * 100:F1}% of the image"),
            ("Inside the design", $"{_s.BackgroundInsideShare * 100:F1}% of the design's circle"),
            ("Sits", _s.BackgroundIsLow ? "below the design: it would be cut deep" : "above the design: it would be left high"),
            ("Reads as", _s.BackgroundLooksLikeFloor ? "a cut-away floor - it reaches well inside the design"
                                                     : "a surround - it is almost all outside the design"))));
        p.Children.Add(Choices(() => _a.Background, v =>
            {
                _a.Background = v;
                if (!_placementTouched) _a.Placement = WizardAdvice.Placement(_s, v, BlankMm);
                RecommendLevels(_a);
            },
            new Opt<BackgroundRole>(BackgroundRole.Surround, "A surround to remove",
                "It is outside the coin, like the backdrop of a photo. It does not set the depth range, and the rim leaves it uncut."
                + (_s.SurroundShaded ? " Because it is not one level, it is first made one, so its shading or marks cannot be mistaken for design." : ""),
                R(BackgroundRole.Surround)),
            new Opt<BackgroundRole>(BackgroundRole.Floor, "Part of the design: a cut-away floor",
                "The design was cut down to it on purpose. It counts when the depth range is set, and is engraved where it lies.", R(BackgroundRole.Floor))));
        p.Children.Add(Why("Black means deepest. A black surround taken for part of the design drags the black point down to "
            + "itself, so the coin never reaches full depth and part of the depth goes on something that is about to be cut "
            + "away. A real cut-away floor taken for a surround is left out when the levels are set. The blue on the picture "
            + "is exactly what DepthView counts as background."));
        return p;
    }

    private Control StepRim()
    {
        var rec = WizardAdvice.Rim(_s);
        string? R(RimChoice r) => r == rec ? "Recommended" : null;
        var p = new StackPanel();
        p.Children.Add(Header("Rim", "How should the edge of the coin be finished?"));
        p.Children.Add(Measured(() => _s.DrawnRim is { } rim
            ? Rows(("Drawn rim", "yes - a raised ring is drawn into the artwork"),
                   ("Width", $"{DrawnRimMm(rim):F2} mm once the blank is sized to it ({rim.Width:F0} px)"),
                   ("Position", $"{rim.Inner:F0} to {rim.Outer:F0} px from the design's centre"),
                   ("Its levels", $"top {rim.TopLevel:N0}, foot {rim.FootLevel:N0}"))
            : Rows(("Drawn rim", "none - the artwork has no raised ring of its own"))));
        p.Children.Add(RimExplainer());
        p.Children.Add(Choices(() => _a.Rim, v => { _a.Rim = v; RecommendLevels(_a); },
            new Opt<RimChoice>(RimChoice.Replace, "Replace the drawn rim with a real one",
                "The blank is centred on the design and sized so the drawn rim lands under a pure white rim. One rim: the blank's own.",
                R(RimChoice.Replace), _s.DrawnRim is not null),
            new Opt<RimChoice>(RimChoice.Add, "Add a rim",
                _s.DrawnRim is null
                    ? "A pure white ring at the edge of the blank, which the laser never touches."
                    : "A pure white ring at the edge of the blank - but the drawn rim stays in the map inside it, and engraves as a second, lower rim with a trench beside it.",
                R(RimChoice.Add)),
            new Opt<RimChoice>(RimChoice.None, "No rim",
                "The map is engraved right to its edges - for a blank with no raised edge, or a design that is not a coin.")));
        p.Children.Add(Section("Rim width"));
        p.Children.Add(Number("Width, measured in from the edge", 0.05m, 10m, 0.05m, "0.00", () => _a.RimMm, v => _a.RimMm = v, "mm",
            () => "Measure your blank's rim with calipers. Many coin blanks are 1 to 1.5 mm.", () => _a.Rim != RimChoice.None));
        p.Children.Add(Why("A blank's rim is untouched metal - the surface every depth is measured from - so in the map it is "
            + "pure white, and the laser never fires there. If the artwork keeps a rim of its own as well, the two sit side by "
            + "side and leave a ring-shaped trench between them where the drawing's outer bevel drops to full depth. Tick "
            + "\"Show the result\" to see the rim your answers make: cyan is the edge of the blank, amber is where engraving stops."));
        return p;
    }

    /// <summary>
    /// What rim replacement is, said plainly before the question is asked. Most coin depth maps
    /// draw a rim; the point of replacing it is that the coin's rim then comes from the blank
    /// itself, which no engraving of a rim can match.
    /// </summary>
    private Control RimExplainer()
    {
        var points = _s.DrawnRim is { } rim
            ? new[]
            {
                ("The drawn rim leaves the job.",
                 "Everything from the foot of the drawn rim outward becomes pure white in the map: an area the laser never goes. "
                 + "It is part of the same file, so it carries into MakeIt or LightBurn with the design - one map, one job, nothing "
                 + "to mask by hand."),
                ("The rim you get is the blank's own.",
                 "Factory-flat, polished, exactly at the blank's height and true to its edge. An engraved copy of a rim comes out "
                 + "lower than the surface, with an ablated finish, and is only as concentric as the job was placed."),
                ("No trench inside the rim.",
                 "A drawn rim slopes down on its outside to the background. Left in the map beside a real rim, that slope cuts a "
                 + "narrow groove to full depth all the way round."),
                ("All of the depth goes to the design.",
                 $"The drawn rim and its bevel stop counting when the depth range is set, so the field and relief get all of it - "
                 + $"here the deepest level of the design moves from {_s.PercentileLevel(0.001, BgIsDesign, false):N0} to "
                 + $"{_s.PercentileLevel(0.001, BgIsDesign, true):N0}."),
                ("The cost.",
                 $"The blank is sized so the drawn rim, about {DrawnRimMm(rim):F2} mm, lands under the real one; anything drawn on "
                 + "that rim - beading, lettering - goes with it. The next steps set the rim's width and how the field rises to meet it."),
            }
            : new[]
            {
                ("The rim is made in the map.",
                 "A ring at the edge of the blank becomes pure white: an area the laser never goes. It is part of the same file, so "
                 + "it carries into MakeIt or LightBurn with the design - one map, one job, nothing to mask by hand."),
                ("The rim you get is the blank's own.",
                 "Factory-flat and polished, exactly at the blank's height. This artwork has no rim of its own to replace, so "
                 + "nothing of the design is given up for it."),
            };

        var list = new StackPanel { Spacing = 8 };
        foreach (var (head, body) in points)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            g.Children.Add(new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = B("#38C8FF"),
                Margin = new Thickness(2, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            });
            var st = new StackPanel { Spacing = 1, Children = { Text(head, 12.5, InkStrong, FontWeight.SemiBold), Text(body, 12, Muted) } };
            Grid.SetColumn(st, 1);
            g.Children.Add(st);
            list.Children.Add(g);
        }
        return new Border
        {
            Background = B("#0F171C"), BorderBrush = B("#1F4656"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 14),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = _s.DrawnRim is null ? "WHAT ADDING A RIM DOES" : "WHAT REPLACING THE RIM DOES",
                                    FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = B("#38C8FF"), LetterSpacing = 1.1 },
                    list,
                },
            },
        };
    }

    private Control StepPlacement()
    {
        var rec = WizardAdvice.Placement(_s, _a.Background, BlankMm);
        string? R(PlacementChoice c) => c == rec ? "Recommended" : null;
        var p = new StackPanel();
        p.Children.Add(Header("Placement", "Where should the blank sit on the image?"));
        p.Children.Add(Measured(() => Rows(
            ("Design centre", $"{_s.CentreX:N0}, {_s.CentreY:N0} px"),
            ("Off centre", $"{WizardAdvice.OffCentreMm(_s, BlankMm):F1} mm from the middle of the image"),
            ("Design size", $"{2 * _s.Radius / CanvasPxPerMm:F1} mm across if the blank spans the short side"),
            ("Background", $"{_s.BackgroundShare * 100:F0}% of the image"))));
        p.Children.Add(Choices(() => _a.Placement, v => { _a.Placement = v; _placementTouched = true; },
            new Opt<PlacementChoice>(PlacementChoice.Design, "Centre the blank on the design",
                "Centred on the design and sized to it, so it fills the blank inside the rim. Only background is ever cropped: a crop that would remove design is refused.",
                R(PlacementChoice.Design)),
            new Opt<PlacementChoice>(PlacementChoice.Grow, "Keep the image's centre, and make room",
                "The pixels are copied into a bigger square, never scaled, until nothing of the design is under the rim. The design comes out smaller on the blank.",
                R(PlacementChoice.Grow)),
            new Opt<PlacementChoice>(PlacementChoice.AsIs, "Keep the image as it is",
                "The blank spans the image's short side, and the rim paints over anything of the design beyond it.")));
        p.Children.Add(LiveAfter(() => _rep?.Fit is { } fit
            ? $"Result: the design spans {fit.ArtAcrossMm:F1} of the {BlankMm:0.#} mm blank." + (_rep!.RimClipped > 0 ? $" The rim covers {_rep.RimClippedFraction * 100:F2}% of it." : "")
            : _rep is { RimClipped: > 0 } r ? $"Result: the rim covers {r.RimClippedFraction * 100:F2}% of the design." : "Result: the rim sits clear of the design."));
        p.Children.Add(Why("A rim is a circle about the blank's centre. On a design drawn off-centre, a rim centred on the image "
            + "comes out lopsided - deep on one side, touching the design on the other. None of these answers resamples the "
            + "map: every original pixel keeps its level, and only how many millimetres it covers changes."));
        return p;
    }

    private TextBlock LiveAfter(Func<string> text)
    {
        var t = Text("", 12.5, Good);
        t.Margin = new Thickness(0, 10, 0, 0);
        _liveAfterRender.Add(() => t.Text = text());
        return t;
    }

    private Control StepEdge()
    {
        var p = new StackPanel();
        p.Children.Add(Header("Edge into the rim", "Should the field step straight up to the rim, or rise into it?"));
        p.Children.Add(Measured(() => Rows(
            ("Rim", $"{_a.RimMm:0.00} mm, pure white"),
            ("Sampling", _a.Target switch
            {
                WizardTarget.MakeIt => "MakeIt sets power every 0.1 mm along a line, in every job DepthView has read",
                WizardTarget.LightBurn => "LightBurn samples at the line interval you set - 0.03 mm suits a 30 um spot",
                _ => "at the slicer's line interval",
            }),
            ("This taper", _a.RampMm <= 0 ? "none: a hard step"
                : _a.Target == WizardTarget.MakeIt ? $"{_a.RampMm:0.00} mm - about {_a.RampMm / 0.1:F0} of MakeIt's samples"
                : $"{_a.RampMm:0.00} mm - about {_a.RampMm / 0.03:F0} lines at 0.03 mm"))));
        p.Children.Add(Choices(() => _a.RampMm > 0, taper =>
            {
                if (taper) _a.RampMm = _lastRamp > 0 ? _lastRamp : WizardAdvice.RampMm(_a.Target);
                else { if (_a.RampMm > 0) _lastRamp = _a.RampMm; _a.RampMm = 0; }
            },
            new Opt<bool>(true, "Taper into the rim",
                "The field rises over the width below to meet the rim, so the engraving does not end in a wall.", "Recommended"),
            new Opt<bool>(false, "Hard step",
                "From field to untouched in one pixel. The beam softens it to about its own width; the wall's angle is whatever ablation makes.")));
        p.Children.Add(Number("Taper width", 0.05m, 3m, 0.05m, "0.00", () => _a.RampMm > 0 ? _a.RampMm : _lastRamp,
            v => { _a.RampMm = v; _lastRamp = v; }, "mm",
            () => _a.Target == WizardTarget.MakeIt && _a.RampMm is > 0 and < 0.3
                ? "Narrower than 0.3 mm reaches MakeIt as only one or two steps."
                : $"Recommended for {TargetName}: {WizardAdvice.RampMm(_a.Target):0.00} mm.",
            () => _a.RampMm > 0));
        p.Children.Add(Why("The map can only say how deep each pixel is, and the beam smears any edge to about its own width. A "
            + "taper wide enough to be sampled several times lets the engraving rise to meet the rim, and where a drawn rim is "
            + "replaced it takes the place of that rim's inner slope. Under the picture is the result's average cross-section "
            + "near the edge: metal below the line, the rim on the right."));
        return p;
    }

    private string TargetName => _a.Target switch
    {
        WizardTarget.MakeIt => "MakeIt",
        WizardTarget.LightBurn => "LightBurn",
        _ => "a slicer",
    };

    /// <summary>The deepest areas (black point) or the highest (white point): one builder, mirrored.</summary>
    private Control StepLevel(bool deepest)
    {
        var ext = deepest ? _s.Floor(BgIsDesign, RimCovered) : _s.Top(BgIsDesign, RimCovered);
        int pct = _s.PercentileLevel(deepest ? 0.001 : 0.999, BgIsDesign, RimCovered);
        int end = _s.PercentileLevel(deepest ? 0 : 1, BgIsDesign, RimCovered);
        var rec = WizardAdvice.Level(ext);
        string? R(LevelChoice c) => c == rec ? "Recommended" : null;
        string point = deepest ? "Black point" : "White point";
        string floorWord = deepest ? "floor" : "flat top";

        var p = new StackPanel();
        p.Children.Add(deepest
            ? Header("Deepest areas", "What should be cut to full depth?")
            : Header("Highest areas", "What should stay untouched - the blank's own surface?"));

        p.Children.Add(Measured(() =>
        {
            var (black, white) = Levels;
            var (n, total) = deepest ? _s.CountIn(0, black, BgIsDesign, RimCovered) : _s.CountIn(white, Max, BgIsDesign, RimCovered);
            var rows = new List<(string, string)>();
            if (ext.Found)
            {
                rows.Add((deepest ? "Floor" : "Flat top", $"{ext.Share * 100:F0}% of the design, levels {ext.Low:N0} to {ext.High:N0}"));
                rows.Add(("Roughness", ext.Noise == 0 ? "none - it is already one level" : $"{ext.Noise:N0} levels across it"));
                rows.Add(("Found from", ext.Source == "background" ? "the background, which you said is part of the design" : $"a nearly level area ({ext.Source})"));
            }
            else rows.Add((deepest ? "Floor" : "Flat top", $"none - no large level area at the {(deepest ? "bottom" : "top")} of the range"));
            if (ext.Source == "gap")
            {
                rows.Add(("Pockets", $"{ext.Share * 100:F2}% of the design, {(deepest ? "below" : "above")} level {(deepest ? ext.Low : ext.High):N0}"));
                rows.Add(("Empty gap", $"levels {ext.Low:N0} to {ext.High:N0} hold nothing - about {GapLayers(ext, pct)} of {_a.Passes} layers would cut nothing new"));
            }
            rows.Add((deepest ? "Deepest 0.1%" : "Highest 0.1%", $"from level {pct:N0}"));
            rows.Add((deepest ? "Lowest level" : "Highest level", $"{end:N0}"));
            rows.Add((deepest ? "Full depth now" : "Untouched now", $"{Pct(n, total)} of the design, level {(deepest ? black : white):N0} and {(deepest ? "below" : "above")}"));
            if (RimCovered) rows.Add(("Counted", "inside the drawn rim only, since it is being replaced"));
            return rows;
        }));

        var opts = new List<Opt<LevelChoice>>();
        if (ext.Found)
        {
            opts.Add(new(LevelChoice.Feature, deepest ? "Make the floor one exact depth" : "Leave the flat top as untouched surface",
                deepest ? $"{point} {ext.Suggested:N0}. The whole floor engraves at one depth" + (ext.Noise > 0 ? $", its {ext.Noise:N0} levels of roughness gone." : ".")
                        : $"{point} {ext.Suggested:N0}. The whole top stays at the blank's surface: no passes at all.", R(LevelChoice.Feature)));
            opts.Add(new(LevelChoice.Percentile, $"Keep the {floorWord}'s texture",
                $"{point} {pct:N0}. Only the extreme 0.1% clips; the {floorWord} keeps its variation and engraves with it.", R(LevelChoice.Percentile)));
        }
        else if (ext.Source == "gap")
        {
            string side = deepest ? "deepest" : "highest";
            opts.Add(new(LevelChoice.Feature, "Close the empty gap",
                $"{point} {ext.Suggested:N0}. The small pockets beyond the gap are already the {side} part of the design and stay so; "
                + $"the {GapLayers(ext, pct)} layers the gap would spend cutting nothing go to the relief instead.", R(LevelChoice.Feature)));
            opts.Add(new(LevelChoice.Percentile, "Keep the gap",
                $"{point} {pct:N0}, the {side} 0.1%. The pockets set the depth range, and the empty gap costs layers that cut nothing new.",
                R(LevelChoice.Percentile)));
        }
        else
        {
            opts.Add(new(LevelChoice.Percentile, deepest ? "The deepest 0.1% becomes full depth" : "The highest 0.1% stays untouched",
                deepest ? $"{point} {pct:N0}. A few stray dark pixels cannot hold the whole relief back."
                        : $"{point} {pct:N0}. The high points of the relief become the blank's own surface.", R(LevelChoice.Percentile)));
            opts.Add(new(LevelChoice.Extreme, "Clip nothing",
                deepest ? $"{point} {end:N0}, the lowest pixel. One stray dark pixel can cost depth everywhere else."
                        : $"{point} {end:N0}, the highest pixel. Everything else gets at least a little cutting."));
        }
        opts.Add(new(LevelChoice.Custom, "Set it by hand", $"Drag the {(deepest ? "red" : "amber")} marker on the histogram below; the picture shows what it takes."));

        p.Children.Add(Choices(() => deepest ? _a.Floor : _a.Top, v =>
            {
                var (black, white) = Levels;
                if (deepest) { _floorTouched = true; _a.Floor = v; if (v == LevelChoice.Custom) _a.BlackCustom = black; }
                else { _topTouched = true; _a.Top = v; if (v == LevelChoice.Custom) _a.WhiteCustom = white; }
            },
            opts.ToArray()));
        p.Children.Add(Strip());
        p.Children.Add(Why(deepest
            ? "Everything at or below the black point is cut to exactly full depth. A floor meant to be flat that varies by even a "
              + "few levels has layer boundaries running through that variation, so it engraves mottled instead of level. Set it "
              + "too high and the deepest detail is lost - watch how much turns red."
            : "White is untouched: the laser never fires there. The highest parts of the relief are usually meant to be the "
              + "blank's own polished surface, level with the rim. A white point a little below the very top costs nothing you "
              + "would see, and keeps the top from being skimmed by a layer that only just reaches it."));
        return p;
    }

    /// <summary>Roughly how many layers an empty gap would spend, with the level point left at the percentile.</summary>
    private int GapLayers(Extreme gap, int percentilePoint)
    {
        var (black, white) = Levels;
        int lo = Math.Min(percentilePoint, black), hi = Math.Max(white, percentilePoint);
        double span = Math.Max(1, hi - lo);
        return (int)Math.Round((gap.High - gap.Low + 1) / span * _a.Passes);
    }

    private Control StepFlat()
    {
        bool anyFloor = _s.FlatAreas.Any(f => f.TouchesFloor);
        var p = new StackPanel();
        p.Children.Add(Header("Flat areas", "Should the parts that are meant to be level engrave level?"));
        p.Children.Add(Measured(() =>
        {
            var (black, white) = Levels;
            double layer = (white - black) / (double)Math.Max(1, _a.Passes - 1);
            return Rows(
                ("Found", $"{_s.FlatAreas.Count} nearly level areas (slope under 0.05% of the range per pixel)"),
                ("The floor", anyFloor ? "is one of them" : "is not among them"),
                ("One layer", $"{layer:N0} source levels at {_a.Passes:N0} layers"),
                ("Pixel noise", $"about {_s.NoiseSigma:F0} levels across the design (fine detail counts too)"));
        }));

        p.Children.Add(Section("Which areas should DepthView look after?"));
        p.Children.Add(Choices(() => _a.FlatScope, v => _a.FlatScope = v,
            new Opt<FlatScope>(FlatScope.Floor, "Only the deepest layer: the floor",
                anyFloor ? "Where noise shows most. A floor engraved at one depth reads as polished metal; a noisy one as frosted."
                         : "No flat area sits at the bottom of this design, so this would change nothing.",
                anyFloor ? "Common" : null, anyFloor),
            new Opt<FlatScope>(FlatScope.All, "Every flat area found",
                "Each area below gets its own recommendation: flatten noise that straddles a layer boundary, smooth noise on a slope, leave real shape alone."),
            new Opt<FlatScope>(FlatScope.None, "None - leave them all as drawn",
                "Nothing is flattened or smoothed.")));

        p.Children.Add(Section("The areas, largest first"));
        p.Children.Add(Text("Click an area to see it alone on the picture; click it again to see them all.", 11.5, Faint));
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        for (int k = 0; k < _s.FlatAreas.Count; k++) list.Children.Add(AreaCard(k));
        p.Children.Add(list);

        p.Children.Add(Why("A level area engraves level only if it stays inside one layer. Pixel noise on it - even a few levels - "
            + "puts it on both sides of a layer boundary in a speckled pattern, and it engraves frosted instead of flat. The "
            + "deepest layer shows this most, which is why the floor is the usual place to start, but any flat area that a "
            + "boundary runs through does the same. Flatten makes an area one exact level, and changes the design to do it; "
            + "Smooth removes only the noise and keeps a real slope. Only pixels inside the area and within its own band of "
            + "levels are touched, so lettering crossing it keeps its depth."));
        return p;
    }

    private Control AreaCard(int k)
    {
        var f = _s.FlatAreas[k];
        if (_a.FlatModes.Length != _s.FlatAreas.Count)
            Array.Resize(ref _a.FlatModes, _s.FlatAreas.Count);

        var title = new TextBlock { Text = $"Area {k + 1}", FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = InkStrong };
        var summary = Text($"{f.ShareOfDesign * 100:F1}% of the design, at level {f.Median:N0}"
                           + (f.TouchesFloor ? " - the floor" : f.TouchesTop ? " - the top" : ""), 12, Muted);
        var suggest = new ContentControl();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(title);
        Grid.SetColumn(suggest, 1); head.Children.Add(suggest);

        var stats = Text("", 12, Ink);
        var why = Text("", 12, Muted);
        why.FontStyle = FontStyle.Italic;

        var buttons = new List<(FlatMode Mode, Button Button)>();
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (mode, label, tip) in new[]
        {
            (FlatMode.Leave, "Leave", "Leave it exactly as drawn."),
            (FlatMode.Smooth, "Smooth", "Remove pixel noise only; a gentle slope or dish survives."),
            (FlatMode.Flatten, "Flatten", "Make it one exact level: the noise and any dish go."),
        })
        {
            var b = new Button
            {
                Content = label, Padding = new Thickness(16, 6), FontSize = 12, MinWidth = 84,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(1), Foreground = Ink,
                CornerRadius = mode == FlatMode.Leave ? new CornerRadius(5, 0, 0, 5) : mode == FlatMode.Flatten ? new CornerRadius(0, 5, 5, 0) : new CornerRadius(0),
            };
            ToolTip.SetTip(b, tip);
            var m = mode;
            b.Click += (_, _) =>
            {
                _flatTouched = true;
                _a.FlatModes[k] = m;
                if (_a.FlatScope == FlatScope.None || !WizardPlan.InScope(f, _a.FlatScope)) _a.FlatScope = FlatScope.All;
                _focusArea = k;
                Changed();
            };
            buttons.Add((mode, b));
            seg.Children.Add(b);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1.5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Spacing = 5, Children = { head, summary, stats, why, seg } },
        };
        card.PointerPressed += (_, _) =>
        {
            _focusArea = _focusArea == k ? -1 : k;
            Changed();
        };

        void Paint()
        {
            var (black, white) = Levels;
            var (recMode, reason) = WizardAdvice.Flat(f, black, white, _a.Passes, Max);
            int crosses = f.BoundariesCrossed(_a.Passes, black, white, Max);
            stats.Text = (f.Spread == 0 ? "Already perfectly level. "
                          : $"Spread {f.Spread:N0} levels, noise {f.Jitter:F0}: {(f.MostlyJitter ? "mostly noise" : "a real slope or dish")}. ")
                       + $"Crosses {WizardAdvice.Plural(crosses, "layer boundary", "layer boundaries")} at {_a.Passes:N0} layers.";
            why.Text = reason;
            suggest.Content = Pill("Suggest: " + recMode, recommended: true);

            bool inScope = WizardPlan.InScope(f, _a.FlatScope);
            var current = inScope ? _a.FlatModes[k] : FlatMode.Leave;
            foreach (var (mode, b) in buttons)
            {
                bool sel = mode == current;
                b.Background = sel ? (mode == FlatMode.Flatten ? B("#1F5A37") : mode == FlatMode.Smooth ? B("#1D4E5A") : B("#3A404B")) : B("#1B1F25");
                b.BorderBrush = sel ? B("#5D9BD3") : B("#333944");
                b.FontWeight = sel ? FontWeight.SemiBold : FontWeight.Normal;
            }
            bool focus = _focusArea == k;
            card.Background = focus ? CardSel : CardBg;
            card.BorderBrush = focus ? CardSelLine : CardLine;
            card.Opacity = inScope ? 1 : 0.55;
        }
        Paint();
        _live.Add(Paint);
        return card;
    }

    /// <summary>Distinct depths the plan gives at its layer count, from the source histogram.</summary>
    private int DistinctDepths(long[] hist, WizardPlan plan)
    {
        var mapped = TuneJob.MapHistogram(hist, Max, plan.Options, out _, out _);
        return TuneJob.DepthsAt(mapped, Max, plan.Passes).Distinct;
    }

    private double _lbPasses = 10;

    private Control StepLayers()
    {
        var p = new StackPanel();
        switch (_a.Target)
        {
            case WizardTarget.MakeIt:
            {
                p.Children.Add(Header("Layers and depth", "How many layers should MakeIt cut?"));
                double z = WizardAdvice.MakeItZStepMm;
                p.Children.Add(Measured(() =>
                {
                    int rec = WizardAdvice.Passes(WizardTarget.MakeIt, DepthMm);
                    int n = _a.Passes;
                    double travel = n * z;
                    int lo = 256 / n, hi = (256 + n - 1) / n;
                    return Rows(
                        ("Target depth", $"{DepthMm:0.00} mm"),
                        ("MakeIt's Z step", $"{z:0.00} mm a layer, in every relief job read so far (3.0.6)"),
                        ("Layers for that", DepthMm / z > WizardAdvice.MakeItMaxLayers
                            ? $"{rec} - MakeIt stops at 256 layers, {256 * z:0.00} mm" : $"{rec}"),
                        ("Z at your count", $"{n} x {z:0.00} = {travel:0.00} mm"
                            + (Math.Abs(travel - DepthMm) < 0.005 ? " - matches the target" : $" - the target is {DepthMm:0.00} mm")),
                        ("Levels per layer", n > 256 ? "more layers than levels" : lo == hi ? $"exactly {lo} of the 256 - even" : $"{lo} or {hi} of the 256 (x{hi / (double)lo:0.00})"),
                        ("Distinct depths", $"{DistinctDepths(_src.Histogram, BuildPlan()):N0} of {n}"));
                }));

                int rec0 = WizardAdvice.Passes(WizardTarget.MakeIt, DepthMm);
                int even = 2;
                while (even * 2 <= rec0) even *= 2;
                var opts = new List<Opt<int>>
                {
                    new(rec0, $"{rec0} layers", $"Reaches {rec0 * z:0.00} mm with Z stepping {z:0.00} mm each layer, so the focus follows the floor down.", "Recommended"),
                };
                if (even != rec0)
                    opts.Add(new(even, $"{even} layers", $"{even * z:0.00} mm deep. Every layer gets exactly {256 / even} of the 256 levels - the evener choice, "
                        + $"if the last {(rec0 - even) * z:0.00} mm of depth does not matter to you."));
                p.Children.Add(Choices(() => _a.Passes, v => { _a.Passes = v; _passesTouched = true; RecommendFlat(_a, scopeToo: false); }, opts.ToArray()));
                p.Children.Add(Number("Or set the count", 2, WizardAdvice.MakeItMaxLayers, 1, "0", () => _a.Passes,
                    v => { _a.Passes = (int)Math.Round(v); _passesTouched = true; }, "", () => "Set the same number as Layers in MakeIt."));
                p.Children.Add(Why("A relief is cut layer by layer, and unless the head comes down by what each layer removes, the "
                    + "focus drifts off the surface being cut: the spot grows, each layer removes less, and the error builds on itself. "
                    + "MakeIt steps Z 0.01 mm a layer, so plan layers x 0.01 mm = depth, then choose power and speed so one layer "
                    + "removes about 10 um. Keep every layer's settings the same, and leave Cleaning Layer off - in 3.0.6 it engraves "
                    + "the previous layer again with no step in Z. The lit view is cut in your layer count: each terrace is one layer."));
                break;
            }

            case WizardTarget.LightBurn:
            {
                p.Children.Add(Header("Layers and depth", "How will LightBurn build up the depth?"));
                p.Children.Add(Measured(() => Rows(
                    ("Mode", "Grayscale: every pass covers the whole image, and power follows the grey"),
                    ("Target depth", $"{DepthMm:0.00} mm"),
                    ("Z step per pass", $"{DepthMm / Math.Max(1, _lbPasses) * 1000:0} um, if {_lbPasses:0} passes reach it"),
                    ("Figures quoted at", $"{_a.Passes:N0} passes in the Tune window - a reference only"))));
                p.Children.Add(Number("Passes you plan to run", 1, 200, 1, "0", () => _lbPasses, v => _lbPasses = v, "",
                    () => "Set the same number in LightBurn, with Z step per pass as above. Measure what one pass removes before trusting it."));
                p.Children.Add(Text("DepthView's layer figures describe a slicer, so they do not describe a Grayscale job. What limits depth "
                    + "resolution there is how many distinct powers LightBurn sends: save the G-code from LightBurn and open it in "
                    + "DepthView to count them.", 12.5, Ink));
                p.Children.Add(Why("Every pass cuts every pixel, deep ones faster than shallow ones, so no single Z step can follow "
                    + "all of them. Set it from the removal at full power, where the surface moves fastest, keep the total depth modest "
                    + "against the lens's depth of focus, and put Min power at the threshold where your material starts to mark - "
                    + "below it nothing is removed, and the top of the relief flattens."));
                break;
            }

            default:
            {
                p.Children.Add(Header("Layers and depth", "How many passes will the slicer cut?"));
                p.Children.Add(Measured(() =>
                {
                    var plan = BuildPlan();
                    return Rows(
                        ("Target depth", $"{DepthMm:0.00} mm"),
                        ("Per pass", $"{DepthMm / Math.Max(1, _a.Passes) * 1000:0.0} um"),
                        ("Distinct depths", $"{DistinctDepths(_src.Histogram, plan):N0} of {_a.Passes:N0} passes"));
                }));
                p.Children.Add(Number("Passes", 2, 65535, 1, "0", () => _a.Passes,
                    v => { _a.Passes = (int)Math.Round(v); _passesTouched = true; }, "", () => "Set the Z step per pass to what one pass actually removes."));
                p.Children.Add(Why("A slicer gives each pass one band of levels, and each band cuts a smaller area than the last. "
                    + "The Z step per pass has to match what one pass removes, or the focus drifts off the surface. The lit view is "
                    + "cut in your pass count: each terrace is one pass."));
                break;
            }
        }
        return p;
    }

    private Control StepOutput()
    {
        bool makeIt = _a.Target == WizardTarget.MakeIt;
        var p = new StackPanel();
        p.Children.Add(Header("Output", "How should the tuned file be written?"));
        p.Children.Add(Measured(() =>
        {
            double ppmm = _rep?.Fit is { } fit ? fit.PixelsPerMm / Scale : CanvasPxPerMm;
            int size = _rep?.Fit is { } f2 ? (int)Math.Round(Math.Max(_src.Width, _src.Height) * (f2.Size / (double)Math.Max(_src.PreviewWidth, _src.PreviewHeight))) : 0;
            return Rows(
                ("Canvas", size > 0 ? $"about {size:N0} px square after placing the blank" : $"{_src.Width:N0} x {_src.Height:N0} px"),
                ("Resolution", $"{ppmm:F1} px/mm, {ppmm * 25.4:N0} dpi"),
                ("Pixel size", $"{1000 / ppmm:F1} um"));
        }));

        p.Children.Add(Section("Bit depth"));
        p.Children.Add(Choices(() => _a.Bits, v => _a.Bits = v,
            new Opt<int>(8, "8-bit", "256 levels - what MakeIt works with. You see exactly the levels it will get, instead of MakeIt reducing them its own way.",
                makeIt ? "Recommended" : null),
            new Opt<int>(16, "16-bit", "Every level kept. The program that drives the laser reduces it once, not twice.",
                makeIt ? null : "Recommended")));

        p.Children.Add(Section("Also"));
        p.Children.Add(Check("Write the resolution into the file", $"The PNG says how big it is, so it imports at exactly {BlankMm:0.#} mm.",
            () => _a.WriteDpi, v => _a.WriteDpi = v, "Recommended"));
        p.Children.Add(Check("Write an alignment outline (SVG)",
            "Circles at the blank's edge and where engraving stops, and a centre mark. An image frames as its rectangle; frame the outline with Hull or Contour instead.",
            () => _a.Outline, v => _a.Outline = v, _a.Target == WizardTarget.LightBurn ? "Recommended" : null));

        p.Children.Add(Section("File name"));
        var stem = System.IO.Path.GetFileNameWithoutExtension(_src.FileName);
        var suffix = new TextBox { Text = _a.Suffix, FontSize = 12.5, Background = WellBg, Foreground = Ink, BorderBrush = B("#333944") };
        suffix.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _a.Suffix = string.Concat((suffix.Text ?? "").Split(System.IO.Path.GetInvalidFileNameChars()));
            Changed();
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var s1 = new TextBlock { Text = stem, FontSize = 12.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(s1);
        Grid.SetColumn(suffix, 1); row.Children.Add(suffix);
        var s2 = new TextBlock { Text = ".png", FontSize = 12.5, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(s2, 2); row.Children.Add(s2);
        p.Children.Add(row);
        p.Children.Add(LiveText(() => $"Offered as {stem}{_a.Suffix}.png, beside the original. {_src.FileName} itself is never modified.", 11.5, Faint));

        p.Children.Add(Why(makeIt
            ? "MakeIt processes depth maps in 8 bits. Writing 8 bits yourself means the 256 levels DepthView reports are the ones "
              + "MakeIt receives. MakeIt sizes the image when you place it, so set it to the blank's diameter there."
            : "There is no 256-layer limit on this path, so nothing is gained by throwing levels away early. With the resolution "
              + "written in, the map imports at its true size - no scaling by hand, which is an easy way to ruin a coin."));
        return p;
    }

    private Control StepReview()
    {
        var plan = BuildPlan();
        var p = new StackPanel();
        p.Children.Add(Header("Review", "Everything the wizard will set, and why."));

        var figures = Text("Working out the figures ...", 12.5, Good);
        figures.Margin = new Thickness(0, 0, 0, 12);
        p.Children.Add(figures);
        FillReviewFigures(figures, plan);

        var list = new StackPanel { Spacing = 9 };
        foreach (var c in plan.Changes)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            g.Children.Add(new TextBlock { Text = "✓", FontSize = 13, Foreground = Good, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeight.SemiBold });
            var st = new StackPanel { Spacing = 1, Children = { Text(c.What, 13, InkStrong, FontWeight.SemiBold), Text(c.Why, 12, Muted) } };
            Grid.SetColumn(st, 1);
            g.Children.Add(st);
            list.Children.Add(g);
        }
        p.Children.Add(list);

        p.Children.Add(Section("The same file from a command line"));
        var cmd = new SelectableTextBlock
        {
            Text = plan.Command, FontFamily = Mono, FontSize = 11.5, Foreground = B("#BFE3CB"), TextWrapping = TextWrapping.Wrap,
        };
        var copy = MakeButton("Copy", primary: false);
        copy.Click += async (_, _) =>
        {
            if (GetTopLevel(this)?.Clipboard is { } cb)
            {
                await cb.SetTextAsync(plan.Command);
                copy.Content = "Copied";
            }
        };
        p.Children.Add(new Border
        {
            Background = WellBg, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = new StackPanel { Spacing = 8, Children = { cmd, copy } },
        });

        var apply = MakeButton("Apply to the Tune window", primary: true);
        apply.Click += (_, _) => Finish(save: false);
        ToolTip.SetTip(apply, "Set the Tune window to these answers. You can still change anything there, and nothing is written until you save.");
        var applySave = MakeButton("Apply and save ...", primary: false);
        applySave.Click += (_, _) => Finish(save: true);
        ToolTip.SetTip(applySave, $"Apply, then open the save dialog with {plan.SuggestedName} offered.");
        p.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 20, 0, 0),
            Children = { apply, applySave },
        });
        p.Children.Add(Text("Run a test piece before cutting a coin you care about.", 11.5, Faint));

        // The wizard is a starting point. Said where the user is about to leave it, because
        // this is the moment someone might think its answers are locked in.
        p.Children.Insert(1, new Border
        {
            Background = B("#14201A"), BorderBrush = B("#2F6B45"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 0, 14),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "YOU HAVE THE FINAL WORD", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = Good, LetterSpacing = 1.1 },
                    Text("These answers set the Tune window's own controls, and nothing more. Every one of them stays yours to "
                       + "change there - move a level point, widen the rim, try another layer count. Tuning of your own beyond "
                       + "what the wizard suggested is exactly what that window is for, and the figures and the preview follow "
                       + "whatever you set. Reset there puts everything back.", 12.5, Ink),
                },
            },
        });
        return p;
    }

    /// <summary>
    /// The before-and-after figures, measured: with flat areas changed, the histogram is taken
    /// from the changed full-resolution map rather than predicted from the original's.
    /// </summary>
    private async void FillReviewFigures(TextBlock target, WizardPlan plan)
    {
        try
        {
            long[] hist = _src.Histogram;
            if (plan.Options.FlatActions.Exists(f => f.Mode != FlatMode.Leave))
            {
                hist = await Task.Run(() =>
                {
                    var g = (ushort[])_src.Grey.Clone();
                    FlatAreas.Apply(g, _src.Width, _src.Height, plan.Options.FlatActions);
                    var h = new long[Max + 1];
                    foreach (var v in g) h[v]++;
                    return h;
                });
            }
            var before = TuneJob.DepthsAt(_src.Histogram, Max, plan.Passes).Distinct;
            var after = DistinctDepths(hist, plan);
            var useBefore = TuneJob.RangeUse(_src.Histogram, Max);
            var useAfter = TuneJob.RangeUse(TuneJob.MapHistogram(hist, Max, plan.Options, out _, out _), Max);
            target.Text = $"At {plan.Passes:N0} {(_a.Target == WizardTarget.LightBurn ? "passes" : "layers")}: {before:N0} distinct depths become {after:N0}, "
                        + $"and the range used goes from {useBefore * 100:F0}% to {useAfter * 100:F0}%.";
        }
        catch (Exception ex)
        {
            target.Text = "The figures could not be worked out: " + ex.Message;
        }
    }

    private void Finish(bool save)
    {
        Plan = BuildPlan();
        SaveAfter = save;
        Preferences.Current.WizardLast = new WizardMemory
        {
            Target = _a.Target,
            WantRim = _a.Rim != RimChoice.None,
            RimMm = _a.RimMm,
            RampMm = _a.RampMm,
            FlatScope = _a.FlatScope,
            Bits = _a.Bits,
            WriteDpi = _a.WriteDpi,
            Outline = _a.Outline,
        };
        Preferences.Current.Save();
        Close();
    }
}
