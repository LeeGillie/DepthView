using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepthView.Analysis;
using DepthView.Controls;
using DepthView.Imaging;
using DepthView.Processing;
using DepthView.Rendering;
using DepthView.Finishing;

namespace DepthView.Views;

/// <summary>
/// Interactive tuning: the original and the corrected map side by side, with the level points
/// draggable on the source histogram and every number that matters recomputed as you move them.
///
/// Two decisions shape the whole window.
///
/// The pictures are computed from a downsampled copy. A 4096 x 4096 map is 16.8 million pixels,
/// and re-running the correction over all of them on every slider tick would make the dialog
/// unusable; at 560 pixels on the long edge it is instant, and a preview is all the picture is
/// for. Nearest-neighbour, not averaging: interpolation would invent levels that are not in the
/// file, which is precisely the sort of quiet lie this program exists to detect.
///
/// The numbers are not computed from that copy. Everything about levels - depths at a pass
/// count, range used, pixels absorbed by each point - comes from putting the source histogram
/// through the same arithmetic, which is exact for the whole image and costs 65,536 additions.
/// Only the rim figures come from the preview, because a rim is geometry rather than levels and
/// a fraction is all anyone wants from it.
/// </summary>
public partial class TuneWindow : Window
{
    private readonly ImageData _image;
    private readonly AnalysisResult _source;
    private readonly string _fileName;
    private readonly string? _sourceDir;
    private readonly string? _sourcePath;

    private readonly ushort[] _grey;
    private readonly int _w, _h, _maxValue;

    private ushort[] _previewGrey = Array.Empty<ushort>();
    private int _pw, _ph;
    private double _previewScale = 1;

    private readonly DispatcherTimer _debounce;
    private bool _loading = true;
    private bool _busy;

    private long[]? _tunedHist;
    private TuningReport? _rimReport;

    private readonly ReliefViewSettings _relief = new();

    private ZScaleHint? _zHint;

    // ---- terraces and the profile line (1.9.0) ----
    //
    // Both need the tuned map at full resolution: a layer edge is a pixel-scale thing, and the
    // reduced preview cannot see one. So whenever either is in use, every recompute also starts
    // a full-resolution pass in the background; a newer recompute cancels an older pass, and a
    // result that arrives for settings no longer on screen is thrown away (the generation).
    private CancellationTokenSource? _fullCts;
    private int _fullGen;
    private ushort[]? _fullTuned;
    private int _fullTW, _fullTH, _fullMax = 65535;
    /// <summary>The last full-resolution check, original and tuned: a TerraceReport, DetailReport, NoiseReport or peak list.</summary>
    private object? _ovOrig, _ovTuned;
    private string _resultBase = "", _origCaptionBase = "", _tunedCaptionBase = "";
    private int _tunedPreviewW, _tunedPreviewH;
    private double _ringBlank, _ringRim;

    /// <summary>The profile line: which pane, and its ends as fractions of that image.</summary>
    private (bool Tuned, double U0, double V0, double U1, double V1)? _line;
    private bool _dragging, _dragTuned;
    private double _dragU0, _dragV0;

    /// <summary>What the pictures are marked with (the Mark box).</summary>
    private enum Mark { None, Terraces, Detail, Noise, Peaks, Aliasing }

    private Mark MarkMode => (Mark)Math.Clamp(OverlayBox.SelectedIndex, 0, 5);

    private bool MarkOn => OverlayBox.SelectedIndex > 0;

    private const int PreviewEdge = 560;

    /// <summary>
    /// Height fields are built smaller than the flat panes. Occlusion is recomputed whenever the
    /// surface changes - which here is every drag of a level marker - and it samples the field
    /// sixteen times per pixel, so the cost of this number is what decides whether the dialog
    /// still feels live. 380 keeps a full recompute in single-digit milliseconds on the field
    /// while losing nothing you could see at pane size.
    /// </summary>
    private const int ReliefEdge = 380;

    /// <summary>
    /// Pass count the dialog opens at, remembered between runs.
    ///
    /// Read once at construction rather than through <see cref="Preferences.Current"/> at every
    /// use, so that changing the box mid-session cannot retroactively change what "Reset" means
    /// while the dialog is still open.
    /// </summary>
    private readonly int _defaultPasses;

    public TuneWindow(ImageData image, AnalysisResult result, string fileName, string? sourcePath)
    {
        InitializeComponent();

        _image = image;
        _source = result;
        _fileName = fileName;
        _sourceDir = sourcePath is null ? null : Path.GetDirectoryName(sourcePath);
        _sourcePath = sourcePath;

        _w = image.Width;
        _h = image.Height;
        _maxValue = image.MaxValue;
        _grey = DepthTuner.ExtractGrey(image);

        BuildPreviewSource();

        SourceText.Text = $"{fileName}   {_w:N0} x {_h:N0}   {image.BitDepth}-bit";

        BlackBox.Maximum = _maxValue;
        WhiteBox.Maximum = _maxValue;

        Strip.SetData(result.GreyHistogram, _maxValue);

        _defaultPasses = Preferences.Current.DefaultPasses;
        PassBox.Value = _defaultPasses;

        var (sb, sw) = DepthTuner.SuggestLevels(result.GreyHistogram);
        ApplyLevels(sb, sw);

        BlackBox.ValueChanged += (_, _) => LevelsTyped();
        WhiteBox.ValueChanged += (_, _) => LevelsTyped();
        Strip.LevelsChanged += (_, _) => LevelsDragged();

        StretchCheck.IsCheckedChanged += (_, _) => Queue();
        InvertCheck.IsCheckedChanged += (_, _) => Queue();
        SurroundCheck.IsCheckedChanged += (_, _) => Queue();
        RimCheck.IsCheckedChanged += (_, _) => Queue();
        FitCheck.IsCheckedChanged += (_, _) => Queue();
        FitPolicyBox.SelectionChanged += (_, _) => Queue();
        CoverRimCheck.IsCheckedChanged += (_, _) => Queue();
        PadBox.SelectionChanged += (_, _) => Queue();
        SliceCheck.IsCheckedChanged += (_, _) => Queue();
        DitherCheck.IsCheckedChanged += (_, _) => Queue();
        MaskCheck.IsCheckedChanged += (_, _) => Queue();
        OutlineCheck.IsCheckedChanged += (_, _) => Queue();
        DpiCheck.IsCheckedChanged += (_, _) => Queue();

        // The blank is shared with every other window, so it is listened to rather than owned:
        // a change made in the relief window lands here too. Diameter turns the drawn depth into
        // millimetres and sets the rim geometry; target depth is what the depth-per-pass figure
        // divides; thickness sets the slab the 3D panes stand on.
        Blank.Current.Changed += OnBlankChanged;
        RimBox.ValueChanged += (_, _) => Queue();
        RampBox.ValueChanged += (_, _) => Queue();
        SpotBox.ValueChanged += (_, _) => Queue();
        PassBox.ValueChanged += (_, _) => { SyncReliefSteps(); RefreshRelief(); Queue(); };
        BitBox.SelectionChanged += (_, _) => Queue();

        StripLogCheck.IsCheckedChanged += (_, _) => Strip.LogScale = StripLogCheck.IsChecked == true;

        SuggestButton.Click += (_, _) => ApplyLevels(sb, sw, refresh: true);
        FullRangeButton.Click += (_, _) => ApplyLevels(0, _maxValue, refresh: true);
        UsedRangeButton.Click += (_, _) => ApplyLevels(result.MinLevel, result.MaxLevel, refresh: true);
        ResetButton.Click += (_, _) => ResetAll(sb, sw);
        SaveButton.Click += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();
        WizardButton.Click += async (_, _) => await OpenWizardAsync();

        // --wizard: open it over this window as soon as this window is up, for screenshots.
        if (Program.StartupWizard)
            Opened += async (_, _) => await OpenWizardAsync();

        WireRelief();
        WireTerraces();

        // Whatever pass count you leave the dialog at is the one it opens at next time, on this
        // machine and every future run. Saved on close rather than on every keystroke, so
        // spinning through 240, 250, 256 does not write the file three times, and so a value
        // typed and then thought better of never becomes the one you inherit.
        Closed += (_, _) =>
        {
            // The blank outlives this window. Leaving the handler attached would keep the closed
            // dialog alive and recomputing every time another window touched the blank.
            Blank.Current.Changed -= OnBlankChanged;
            Blank.Current.SaveIfChanged();
            _fullCts?.Cancel();

            int passes = (int)(PassBox.Value ?? _defaultPasses);
            if (passes == Preferences.Current.DefaultPasses) return;

            Preferences.Current.DefaultPasses = passes;
            Preferences.Current.Save();
        };

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Recompute(); };

        ApplyStartupOverrides();

        // --window sizes this dialog too, deliberately allowed below its own minimum. The
        // layout that breaks first is the one at the smallest size anybody runs, and the only
        // way to see it is to open it there - which is not something a developer with a large
        // monitor will ever do by accident.
        if (Program.WindowWidth is int fw && Program.WindowHeight is int fh)
        {
            MinWidth = Math.Min(MinWidth, fw);
            MinHeight = Math.Min(MinHeight, fh);
            Width = fw;
            Height = fh;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(60, 60);
        }

        _loading = false;
        RenderOriginal();
        Recompute();
    }

    /// <summary>
    /// Settings handed in on the command line, so the dialog can be opened already configured
    /// for a blank you cut regularly - and so the rim layout can be captured by a script.
    /// </summary>
    private void ApplyStartupOverrides()
    {
        // --blank, --thick and --depth-mm were applied to the shared blank before any window
        // opened, so there is nothing to copy here.
        if (Program.StartupExagStops is double stops)
            ReliefExagSlider.Value = Math.Clamp(stops, ZScale.MinStops, ZScale.MaxStops);
        if (Program.StartupRampMm is double ramp && ramp >= 0) RampBox.Value = (decimal)ramp;
        if (Program.StartupPasses is int passes && passes >= 2) PassBox.Value = passes;

        if (Program.StartupRimMm is double rim && rim > 0)
        {
            RimBox.Value = (decimal)rim;
            RimCheck.IsChecked = true;
        }

        if (Program.StartupFit != FitPolicy.None)
        {
            FitCheck.IsChecked = true;
            FitPolicyBox.SelectedIndex = Program.StartupFit switch
            {
                FitPolicy.Canvas => 1,
                FitPolicy.Design => 2,
                _ => 0,
            };
        }
        if (Program.StartupCoverRim) CoverRimCheck.IsChecked = true;
        if (Program.StartupUniformSurround) SurroundCheck.IsChecked = true;

        if (Program.StartupBlack is not null || Program.StartupWhite is not null)
            ApplyLevels(Program.StartupBlack ?? (int)(BlackBox.Value ?? 0),
                        Program.StartupWhite ?? (int)(WhiteBox.Value ?? _maxValue));
        if (Program.StartupBits is int bits) BitBox.SelectedIndex = bits == 8 ? 1 : 0;
        if (Program.StartupSpot is double spot && spot > 0) SpotBox.Value = (decimal)spot;
        if (Program.StartupOutline) OutlineCheck.IsChecked = true;
        if (Program.StartupWriteDpi) DpiCheck.IsChecked = true;

        // --relief alongside --tune-ui opens straight into the lit view. Same reason as every
        // other override here: a screenshot of the 3D panes has to be capturable headlessly,
        // or the only thing standing between a broken layout and a release is somebody
        // remembering to tick a box on one machine.
        if (Program.StartupRelief) ReliefCheck.IsChecked = true;

        // --show-terraces and --profile-line, for the same reason: so the terrace view and a
        // profile can be captured by a script.
        if (Program.StartupTerraces) OverlayBox.SelectedIndex = 1;
        if (Program.StartupMark is int mk) OverlayBox.SelectedIndex = Math.Clamp(mk, 0, 5);
        if (Program.StartupFinish is { } fin)
            Dispatcher.UIThread.Post(() => OpenFinishing(fin), DispatcherPriority.Background);
        if (Program.StartupProfileLine is { Length: 4 } pl)
        {
            _line = (true, pl[0], pl[1], pl[2], pl[3]);
            TunedLine.Set(pl[0], pl[1], pl[2], pl[3]);
        }
    }

    // ------------------------------------------------------------------ terraces and the line

    private void WireTerraces()
    {
        OverlayBox.SelectionChanged += (_, _) =>
        {
            // The lit panes and the marked view both take over the pictures; one at a time.
            if (MarkOn && ReliefOn) ReliefCheck.IsChecked = false;
            _ovOrig = _ovTuned = null;
            if (!MarkOn) RenderOriginal();
            Recompute();
        };

        WirePane(OriginalPane, OriginalLine, tuned: false);
        WirePane(TunedPane, TunedLine, tuned: true);
        ProfileClearButton.Click += (_, _) => ClearLine();
    }

    /// <summary>Drag across a picture to lay a line on it; release to plot the depth along it.</summary>
    private void WirePane(Grid pane, PaneLine line, bool tuned)
    {
        (double U, double V)? Where(Avalonia.Input.PointerEventArgs e, bool clamp)
        {
            var p = e.GetPosition(line);
            var r = line.ImageRect();
            if (r.Width <= 0 || r.Height <= 0) return null;
            double u = (p.X - r.X) / r.Width, v = (p.Y - r.Y) / r.Height;
            if (!clamp && (u < 0 || v < 0 || u > 1 || v > 1)) return null;
            return (Math.Clamp(u, 0, 1), Math.Clamp(v, 0, 1));
        }

        pane.PointerPressed += (_, e) =>
        {
            if (ReliefOn || !e.GetCurrentPoint(pane).Properties.IsLeftButtonPressed) return;
            if (Where(e, clamp: false) is not { } at) return;
            _dragging = true;
            _dragTuned = tuned;
            (_dragU0, _dragV0) = at;
            (tuned ? OriginalLine : TunedLine).Clear();
            line.Set(at.U, at.V, at.U, at.V);
            e.Pointer.Capture(pane);
            e.Handled = true;
        };
        pane.PointerMoved += (_, e) =>
        {
            if (!_dragging || _dragTuned != tuned || Where(e, clamp: true) is not { } at) return;
            line.Set(_dragU0, _dragV0, at.U, at.V);
        };
        pane.PointerReleased += (_, e) =>
        {
            if (!_dragging || _dragTuned != tuned) return;
            _dragging = false;
            e.Pointer.Capture(null);
            if (Where(e, clamp: true) is not { } at) return;
            if (Math.Abs(at.U - _dragU0) < 0.01 && Math.Abs(at.V - _dragV0) < 0.01) { ClearLine(); return; }
            _line = (tuned, _dragU0, _dragV0, at.U, at.V);
            if (tuned && _fullTuned is null) StartFullPass(Build());
            UpdateProfile();
        };
    }

    private void ClearLine()
    {
        _line = null;
        OriginalLine.Clear();
        TunedLine.Clear();
        Profile.Clear();
        ProfilePanel.IsVisible = false;
    }

    /// <summary>
    /// Plot the file's own levels along the line - full resolution, nearest neighbour - against
    /// the staircase they will be cut as. The original is always to hand; the tuned map waits
    /// for the background pass.
    /// </summary>
    private void UpdateProfile()
    {
        if (_line is not { } l) { ProfilePanel.IsVisible = false; return; }
        ProfilePanel.IsVisible = true;

        ushort[] src;
        int w, h, max;
        if (!l.Tuned) { src = _grey; w = _w; h = _h; max = _maxValue; }
        else if (_fullTuned is { } ft) { src = ft; w = _fullTW; h = _fullTH; max = _fullMax; }
        else
        {
            Profile.Clear();
            ProfileCaption.Text = "Measuring the tuned map at full resolution ...";
            return;
        }

        double x0 = l.U0 * (w - 1), y0 = l.V0 * (h - 1), x1 = l.U1 * (w - 1), y1 = l.V1 * (h - 1);
        var levels = TerraceMap.Profile(src, w, h, x0, y0, x1, y1);
        double ppmm = Math.Min(w, h) / Blank.Current.DiameterMm;
        double lengthMm = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / ppmm;
        int passes = (int)(PassBox.Value ?? 256);
        double spot = (double)(SpotBox.Value ?? 7);

        Profile.SetData(levels, max, passes, lengthMm, Blank.Current.TargetDepthMm, spot,
                        $"{(l.Tuned ? "Tuned" : "Original")}: depth along the line");
        ProfileCaption.Text = Profile.EdgesCrossed == 0
            ? $"No layer edge along this line at {passes:N0} passes."
            : $"{Profile.EdgesCrossed:N0} layer edges along this line; the treads between them are a median"
              + $" {Profile.MedianTreadMicrons:0} um along it, against a {spot:0} um spot."
              + " A line crossing the contours at a slant reads them wider than they are.";
    }

    /// <summary>
    /// The full-resolution pass behind the terrace view and the tuned profile. Started by every
    /// recompute while either is in use; a newer one cancels it.
    /// </summary>
    private void StartFullPass(TuningOptions full)
    {
        var mark = ReliefOn ? Mark.None : MarkMode;
        bool needTuned = mark != Mark.None || _line is { Tuned: true };
        _fullCts?.Cancel();
        if (!needTuned) return;

        var cts = _fullCts = new CancellationTokenSource();
        int gen = ++_fullGen;
        int passes = (int)(PassBox.Value ?? 256);
        double spot = (double)(SpotBox.Value ?? 7);
        double blank = Blank.Current.DiameterMm, depth = Blank.Current.TargetDepthMm;
        int pw = _tunedPreviewW, ph = _tunedPreviewH;
        _fullTuned = null;
        if (_line is { Tuned: true }) UpdateProfile();

        var token = cts.Token;
        Task.Run(() =>
        {
            // A newer recompute cancels this one all the time - every keystroke in a number
            // box. That is routine, not an error, so it ends here with a null result rather
            // than as an exception: a throw escaping the lambda stops the debugger as
            // "user-unhandled" on every edit even though nothing is wrong.
            try { return FullPass(full, mark, passes, spot, blank, depth, pw, ph, token); }
            catch (OperationCanceledException) { return null; }
        }, token).ContinueWith(t =>
        {
            if (t.IsCanceled || t.IsFaulted || t.Result is not { } r) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _fullGen) return;
                _fullTuned = r.tuned;
                _fullTW = r.tw;
                _fullTH = r.th;
                _fullMax = r.tmax;

                if (r.ro is not null && r.rt is not null && !ReliefOn && MarkMode == r.mark)
                {
                    _ovOrig = r.ro;
                    _ovTuned = r.rt;
                    // The original as it would be cut as it stands: the blank spans its short
                    // side. Only that circle is measured, so it is drawn.
                    OriginalImage.Source = FinishBitmap(r.oo!, _pw, _ph, Math.Min(_pw, _ph) / 2.0, 0);
                    TunedImage.Source = FinishBitmap(r.ot!, pw, ph, _ringBlank, _ringRim);
                    OriginalCaption.Text = _origCaptionBase + MarkCaption(r.ro);
                    TunedCaption.Text = _tunedCaptionBase + MarkCaption(r.rt);
                    ResultText.Text = _resultBase + Environment.NewLine + string.Join(Environment.NewLine, MarkRows());
                }
                UpdateProfile();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The work behind <see cref="StartFullPass"/>, off the UI thread. Returns null when
    /// cancelled between stages; a cancel inside a measurement surfaces as
    /// OperationCanceledException, which the caller catches.
    /// </summary>
    private (ushort[] tuned, int tw, int th, int tmax, Mark mark, object? ro, object? rt, byte[]? oo, byte[]? ot)?
        FullPass(TuningOptions full, Mark mark, int passes, double spot, double blank, double depth,
                 int pw, int ph, CancellationToken token)
    {
        {
            var tuned = DepthTuner.Apply(_grey, _w, _h, _maxValue, full, out var rep);
            if (token.IsCancellationRequested) return null;
            int tw = rep.OutWidth, th = rep.OutHeight;

            // The file as it will be written: an 8-bit output terraces on its own 256 steps,
            // which the in-memory map at the source's precision would hide.
            int tmax = _maxValue;
            if (full.OutputBitDepth == 8 && _maxValue != 255)
            {
                tuned = TuneJob.ScaleForOutput(tuned, _maxValue, 8);
                tmax = 255;
            }

            object? ro = null, rt = null;
            byte[]? oo = null, ot = null;
            double ppO = Math.Min(_w, _h) / blank, ppT = Math.Min(tw, th) / blank;
            double bO = Math.Min(_w, _h) / 2.0, bT = Math.Min(tw, th) / 2.0;
            switch (mark)
            {
                case Mark.Terraces:
                {
                    var to = TerraceMap.Measure(_grey, _w, _h, _maxValue, passes, ppO, depth, spot, true, token);
                    var tt = TerraceMap.Measure(tuned, tw, th, tmax, passes, ppT, depth, spot, true, token);
                    oo = TerraceMap.Overlay(_grey, _w, _h, _maxValue, to.Classes!, _pw, _ph, to.BlankRadiusPx);
                    ot = TerraceMap.Overlay(tuned, tw, th, tmax, tt.Classes!, pw, ph, tt.BlankRadiusPx);
                    to.Classes = null; tt.Classes = null;
                    ro = to; rt = tt;
                    break;
                }
                case Mark.Detail:
                {
                    var dO = DetailMap.Measure(_grey, _w, _h, _maxValue, passes, ppO, depth, spot, true, token);
                    var dT = DetailMap.Measure(tuned, tw, th, tmax, passes, ppT, depth, spot, true, token);
                    oo = ClassOverlay.Draw(_grey, _w, _h, _maxValue, dO.Classes!, _pw, _ph, bO);
                    ot = ClassOverlay.Draw(tuned, tw, th, tmax, dT.Classes!, pw, ph, bT);
                    dO.Classes = null; dT.Classes = null;
                    ro = dO; rt = dT;
                    break;
                }
                case Mark.Noise:
                {
                    var nO = NoiseMap.Measure(_grey, _w, _h, _maxValue, passes, ppO, depth, true, token);
                    var nT = NoiseMap.Measure(tuned, tw, th, tmax, passes, ppT, depth, true, token);
                    oo = ClassOverlay.Draw(_grey, _w, _h, _maxValue, nO.Classes!, _pw, _ph, bO);
                    ot = ClassOverlay.Draw(tuned, tw, th, tmax, nT.Classes!, pw, ph, bT);
                    nO.Classes = null; nT.Classes = null;
                    ro = nO; rt = nT;
                    break;
                }
                case Mark.Peaks:
                {
                    Func<int, int, bool> Inside(int w, int h) { double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, r2 = Math.Min(w, h) * Math.Min(w, h) / 4.0; return (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2; }
                    var pO = FlatPeaks.Find(_grey, _w, _h, _maxValue, Inside(_w, _h));
                    token.ThrowIfCancellationRequested();
                    var pT = FlatPeaks.Find(tuned, tw, th, tmax, Inside(tw, th));
                    oo = ClassOverlay.Draw(_grey, _w, _h, _maxValue, FlatPeaks.Classes(_grey, _w, _h, pO), _pw, _ph, bO, gain: 40);
                    ot = ClassOverlay.Draw(tuned, tw, th, tmax, FlatPeaks.Classes(tuned, tw, th, pT), pw, ph, bT, gain: 40);
                    ro = pO; rt = pT;
                    break;
                }
                case Mark.Aliasing:
                {
                    var aO = EdgeAlias.Measure(_grey, _w, _h, _maxValue, ppO, true, token);
                    var aT = EdgeAlias.Measure(tuned, tw, th, tmax, ppT, true, token);
                    oo = ClassOverlay.Draw(_grey, _w, _h, _maxValue, aO.Classes!, _pw, _ph, bO, gain: 40);
                    ot = ClassOverlay.Draw(tuned, tw, th, tmax, aT.Classes!, pw, ph, bT, gain: 40);
                    aO.Classes = null; aT.Classes = null;
                    ro = aO; rt = aT;
                    break;
                }
            }
            return (tuned, tw, th, tmax, mark, ro, rt, oo, ot);
        }
    }

    /// <summary>The tuning guide that shipped with this version (see <see cref="Guides"/>).</summary>
    private async void OnTuningGuide(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await Guides.OpenAsync(TopLevel.GetTopLevel(this), Guides.Kind.Tuning) is { } problem)
            SubtitleText.Text = problem;
    }

    /// <summary>
    /// The finishing preview, on the map these controls would write, at full resolution.
    /// It builds its own copy from the settings as they stand, so later changes here do not
    /// reach into it.
    /// </summary>
    private void OnFinishing(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenFinishing(null);

    private void OpenFinishing(string? spec)
    {
        FinishRecipe? recipe = null;
        if (!string.IsNullOrWhiteSpace(spec)) recipe = FinishRecipe.Parse(spec, out _);
        Finishing = new FinishWindow(_grey, _w, _h, _maxValue, Build(), _fileName, recipe);
        Finishing.Show(this);
    }

    /// <summary>The finishing window last opened from here, for --finish-ui screenshots.</summary>
    public FinishWindow? Finishing { get; private set; }

    /// <summary>The caption line for whatever the pictures are marked with.</summary>
    private static string MarkCaption(object? report) => report switch
    {
        TerraceReport t => TerraceCaption(t),
        DetailReport d => d.FinerThanPixels
            ? "\nThe spot is narrower than a pixel: nothing in this file is finer than the beam."
            : $"\nDetail finer than the spot: {d.ShareUnderSpot * 100:F2}% of the blank (red), {d.ShareUnder2Spots * 100:F2}% more under two spots (amber).",
        NoiseReport n => n.Noisy == 0
            ? "\nNo noise of half a layer or more on any smooth surface."
            : $"\nNoise on smooth surfaces: {n.ShareNoisy * 100:F1}% of the blank, median {n.MedianNoiseMicrons:F1} um (amber: half a layer, red: two).",
        List<FlatPeak> p => p.Count == 0
            ? "\nNo flattened peaks: every summit is explained by the slope below it."
            : $"\nFlattened peaks: {p.Count} (red), the largest {p[0].Pixels:N0} px - flat tops the slope below does not explain.",
        AliasReport a => a.EdgePixels < EdgeAlias.MinEdges
            ? "\nToo few diagonal or curved step edges to judge."
            : $"\nJagged edges: {a.Share * 100:F0}% of the diagonal and curved step edges jump a whole step in one pixel (red)"
              + (a.Jagged ? " - rendered at its final size; export larger and reduce." : "."),
        _ => "",
    };

    /// <summary>The rows of the results card for the current mark, original to tuned.</summary>
    private IEnumerable<string> MarkRows()
    {
        switch (_ovOrig, _ovTuned)
        {
            case (TerraceReport, TerraceReport):
                foreach (var row in TerraceRows()) yield return row;
                break;
            case (DetailReport a, DetailReport b):
                yield return $"Detail under the spot  {a.ShareUnderSpot * 100:F2}%  to  {b.ShareUnderSpot * 100:F2}% of the blank";
                yield return $"Under two spots        {a.ShareUnder2Spots * 100:F2}%  to  {b.ShareUnder2Spots * 100:F2}%";
                break;
            case (NoiseReport a, NoiseReport b):
                yield return $"Noisy smooth surface   {a.ShareNoisy * 100:F1}%  to  {b.ShareNoisy * 100:F1}% of the blank";
                yield return $"Median noise           {a.MedianNoiseMicrons:F1} um  to  {b.MedianNoiseMicrons:F1} um";
                break;
            case (List<FlatPeak> a, List<FlatPeak> b):
                yield return $"Flattened peaks        {a.Count}  to  {b.Count}";
                break;
            case (AliasReport a, AliasReport b):
                yield return $"Jagged edges           {a.Share * 100:F0}%  to  {b.Share * 100:F0}% of the step edges";
                break;
        }
    }

    private static string MarkTitle(Mark m) => m switch
    {
        Mark.Terraces => "Steps that will show",
        Mark.Detail => "Detail under the spot",
        Mark.Noise => "Noisy smooth surface",
        Mark.Peaks => "Flattened peaks",
        Mark.Aliasing => "Jagged edges",
        _ => "",
    };

    private static string TerraceCaption(TerraceReport r)
        => r.Edges == 0
            ? "\nNo layer edges at this pass count."
            : $"\nLayer edges that will show: {r.ShareWider * 100:F0}% of them have treads wider than the spot"
            + $" (amber), {r.ShareWider3 * 100:F0}% wider than three spots (red).";

    /// <summary>The terrace rows of the results card, original to tuned.</summary>
    private IEnumerable<string> TerraceRows()
    {
        if (_ovOrig is not TerraceReport a || _ovTuned is not TerraceReport b) yield break;
        yield return $"Steps that will show   {a.ShareWider * 100:F0}%  to  {b.ShareWider * 100:F0}% of the layer edges";
        yield return $"Length of those steps  {a.EdgeLengthMm * a.ShareWider:N0} mm  to  {b.EdgeLengthMm * b.ShareWider:N0} mm";
        yield return b.LimitedByLevels
            ? "To blend 9 in 10       not by passes: the map's own steps"
            : b.PassesToBlend90 is int need && need > b.Passes
            ? $"To blend 9 in 10       about {need:N0} passes"
            : "To blend 9 in 10       already, at this spot";
    }

    // ------------------------------------------------------------------ relief panes

    /// <summary>
    /// The lit view is a second way of looking at the same two buffers, not a second pipeline.
    /// Both panes are handed the exact arrays the flat panes are drawn from - the original
    /// preview reduction, and whatever <see cref="Recompute"/> last produced - so the 3D view
    /// cannot show a surface the flat view disagrees with.
    /// </summary>
    private void WireRelief()
    {
        foreach (var m in MaterialLibrary.Presets)
            ReliefMaterialBox.Items.Add(new ComboBoxItem { Content = m.Name });
        ReliefMaterialBox.SelectedIndex = 0;

        OriginalRelief.Settings = _relief;
        TunedRelief.Settings = _relief;

        // One camera, two panes. A drag in either one has already mutated the shared settings
        // by the time this fires; the other pane just needs telling to draw again.
        OriginalRelief.ViewChanged += (_, _) => TunedRelief.Request(true);
        TunedRelief.ViewChanged += (_, _) => OriginalRelief.Request(true);

        ReliefCheck.IsCheckedChanged += (_, _) => ReliefModeChanged();

        ReliefMaterialBox.SelectionChanged += (_, _) =>
        {
            _relief.MaterialIndex = ReliefMaterialBox.SelectedIndex;
            RefreshRelief();
        };

        _zHint = new ZScaleHint(ReliefExagSlider, ReliefExagLabel, ReliefExagVerdict);
        ReliefExagSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            if (_zHint.Snap()) return;
            SyncReliefDepth();
            RefreshRelief();
        };
        ReliefTrueScaleButton.Click += (_, _) => _zHint.ToTrueScale();

        ReliefLightSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _relief.LightAzimuthDeg = ReliefLightSlider.Value;
            RefreshRelief();
        };

        ReliefTiltCheck.IsCheckedChanged += (_, _) =>
        {
            _relief.Orbit = ReliefTiltCheck.IsChecked == true;
            RefreshRelief();
        };

        // Terracing follows the pass count rather than getting a control of its own. There is
        // already exactly one pass count in this dialog and every other number is quoted
        // against it; a second, independent step count would be a way to look at a staircase
        // the job will never cut.
        ReliefStepCheck.IsCheckedChanged += (_, _) => { SyncReliefSteps(); RefreshRelief(); Queue(); };

        ReliefResetButton.Click += (_, _) => { _relief.ResetView(); RefreshRelief(); };
    }

    private void SyncReliefSteps()
        => _relief.SliceCount = ReliefStepCheck.IsChecked == true ? (int)(PassBox.Value ?? 256) : 0;

    /// <summary>
    /// Turn the target depth and the exaggeration stops into one drawn depth, and say plainly
    /// what that depth is - in words, and in the green-to-red colour of the slider and the badge
    /// on each pane.
    ///
    /// Stops rather than a multiplier because the interesting range spans three orders of
    /// magnitude - a coin relief is tenths of a millimetre and the old default was drawing five
    /// - and a linear multiplier over that span is unusable at the shallow end, which is the end
    /// that matters. 0 is the depth the user entered, so "no exaggeration" finally means
    /// something a caliper could check. The arithmetic lives in <see cref="ZScale"/>, shared with
    /// the standalone relief window and --render so the three cannot drift apart again.
    /// </summary>
    private void SyncReliefDepth()
    {
        double stops = ReliefExagSlider.Value;
        double target = Blank.Current.TargetDepthMm;
        double blank = Blank.Current.DiameterMm;

        // The bottom of the travel is a hard zero rather than another halving. Somewhere to put
        // the slider that answers "is this shape in the map at all, or am I looking at shading?"
        bool flat = ZScale.IsFlat(stops);
        double depth = ZScale.DrawnDepthMm(target, stops);

        _relief.ApparentDepthMm = depth;
        _relief.BlankMm = blank;
        _relief.ZStops = stops;
        _relief.SlabRatio = Blank.Current.ThicknessMm / target;

        string factor = ZScale.FactorText(stops);

        string shown = flat ? "no depth at all"
                     : $"{depth:0.00#} mm deep on a {blank:0.#} mm blank";

        _zHint?.Paint();
        ReliefExagVerdict.Text = ZScale.Verdict(stops);

        // Broken deliberately rather than left to wrap: the factor and the millimetres are two
        // separate facts and the panel is narrow enough that the wrap point moves as you drag.
        ReliefExagLabel.Text = $"Vertical exaggeration   {factor}\n{shown}";
    }

    private bool ReliefOn => ReliefCheck.IsChecked == true;

    private void OnBlankChanged(object? sender, EventArgs e)
    {
        SyncReliefDepth();
        RefreshRelief();
        Queue();
    }

    private void ReliefModeChanged()
    {
        bool on = ReliefOn;
        if (on && MarkOn) OverlayBox.SelectedIndex = 0;
        if (on) { OriginalLine.IsVisible = false; TunedLine.IsVisible = false; }
        else { OriginalLine.IsVisible = true; TunedLine.IsVisible = true; }
        ReliefPanel.IsVisible = on;

        OriginalImage.IsVisible = !on;
        TunedImage.IsVisible = !on;
        OriginalRelief.IsVisible = on;
        TunedRelief.IsVisible = on;

        if (!on)
        {
            // Drop both scenes rather than keeping them warm. They hold an occlusion map each,
            // and someone who switched the 3D view off is not about to need them back inside a
            // frame or two.
            OriginalRelief.Clear();
            TunedRelief.Clear();
            Recompute();   // strips the step note from both captions
            return;
        }

        SyncReliefSteps();
        SyncReliefDepth();
        PushOriginalRelief();
        Recompute();
    }

    private void RefreshRelief()
    {
        if (_loading || !ReliefOn) return;
        OriginalRelief.Request(true);
        TunedRelief.Request(true);
    }

    private void PushOriginalRelief()
    {
        if (!ReliefOn) return;
        var f = ReliefRenderer.BuildHeights(_previewGrey, _pw, _ph, _maxValue,
                                            ReliefEdge, out int w, out int h);
        OriginalRelief.SetField(f, w, h);
    }

    private void PushTunedRelief(ushort[] tuned, int w, int h)
    {
        if (!ReliefOn) return;
        var f = ReliefRenderer.BuildHeights(tuned, w, h, _maxValue,
                                            ReliefEdge, out int fw, out int fh);
        TunedRelief.SetField(f, fw, fh);
    }

    // ------------------------------------------------------------------ preview source

    /// <summary>
    /// One nearest-neighbour reduction, kept for the life of the window. Both panes are drawn
    /// from it, so what you compare is the same sampling of the same pixels with and without
    /// the correction, and no difference on screen can be an artefact of the downsampling.
    /// </summary>
    private void BuildPreviewSource()
    {
        _previewScale = Math.Min(1.0, (double)PreviewEdge / Math.Max(_w, _h));
        _pw = Math.Max(1, (int)Math.Round(_w * _previewScale));
        _ph = Math.Max(1, (int)Math.Round(_h * _previewScale));
        _previewGrey = new ushort[(long)_pw * _ph];

        for (int y = 0; y < _ph; y++)
        {
            int sy = (int)((long)y * _h / _ph);
            long srow = (long)sy * _w;
            int drow = y * _pw;
            for (int x = 0; x < _pw; x++)
                _previewGrey[drow + x] = _grey[srow + (int)((long)x * _w / _pw)];
        }
    }

    private void RenderOriginal()
    {
        OriginalImage.Source = ToBitmap(_previewGrey, _pw, _ph);
        OriginalCaption.Text = OriginalLevelsText();
    }

    private string OriginalLevelsText()
    {
        var (min, max, unique) = TuneJob.Span(_source.GreyHistogram);
        return $"{unique:N0} levels, {min:N0} to {max:N0}, "
             + $"{TuneJob.RangeUse(_source.GreyHistogram, _maxValue) * 100:F0}% of the range";
    }

    /// <summary>
    /// What the terraced pane is actually showing, when it is showing terraces.
    ///
    /// The step count is the depth count, not the pass count: asking for 256 passes on a map
    /// that only occupies a third of the range still lands every one of its levels inside about
    /// a third of the steps. Printing the pass count here would say the two panes were the same,
    /// which is the exact misreading the note exists to prevent.
    /// </summary>
    private string StepNote(int depths, int passes)
        => ReliefOn && ReliefStepCheck.IsChecked == true
            ? $"\n{depths:N0} steps of the {passes:N0} passes"
            : "";

    /// <summary>
    /// Render a grey buffer at its own dimensions, which are not always the source's: fitting
    /// grows the canvas. Both panes are then stretched to the same box on screen, and that is
    /// the honest comparison, because in both panes the frame means the same thing - the whole
    /// blank. The artwork visibly occupying less of the tuned frame is not a drawing artefact;
    /// it is exactly what happens to the coin.
    /// </summary>
    private WriteableBitmap ToBitmap(ushort[] grey, int w, int h,
                                     double blankRadius = 0, double rimInner = 0)
    {
        var buf = new byte[(long)w * h * 4];
        for (long i = 0; i < grey.Length; i++)
        {
            byte v = (byte)Math.Clamp((long)grey[i] * 255 / Math.Max(1, _maxValue), 0, 255);
            long d = i * 4;
            buf[d] = v; buf[d + 1] = v; buf[d + 2] = v; buf[d + 3] = 255;
        }
        return FinishBitmap(buf, w, h, blankRadius, rimInner);
    }

    /// <summary>A BGRA buffer onto the screen, with the blank and rim rings drawn in.</summary>
    private static WriteableBitmap FinishBitmap(byte[] buf, int w, int h, double blankRadius, double rimInner)
    {

        // Two rings, drawn in colour on a greyscale preview so they cannot be mistaken for
        // data - nothing in a depth map is ever cyan.
        //
        // They mark the boundary the file itself cannot express. Past the rim the map is all
        // one value, so the edge of the blank is white on white and invisible, which makes the
        // untouched rim and the corners of the square look like the same thing. They are not:
        // the rim is part of the coin and is deliberately left uncut, while the corners are
        // not on the coin at all. Same pixel value, different reasons, and worth being able
        // to see the difference before sending the job.
        if (blankRadius > 0)
        {
            Ring(buf, w, h, blankRadius, 0x38, 0xC8, 0xFF);   // cyan: the edge of the blank
            if (rimInner > 0 && rimInner < blankRadius)
                Ring(buf, w, h, rimInner, 0xF0, 0xB4, 0x5C);  // amber: where engraving stops
        }

        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(buf, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }
        return bmp;
    }

    /// <summary>
    /// A one-pixel circle drawn straight into the BGRA buffer. Scanned per row rather than
    /// stepped round the circumference, so it stays continuous at any radius instead of
    /// leaving gaps where a parametric walk would skip pixels.
    /// </summary>
    private static void Ring(byte[] buf, int w, int h, double radius, byte r, byte g, byte b)
    {
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        int lo = Math.Max(0, (int)(cy - radius) - 1), hi = Math.Min(h - 1, (int)(cy + radius) + 1);

        for (int y = lo; y <= hi; y++)
        {
            double dy = y - cy;
            double inside = radius * radius - dy * dy;
            if (inside < 0) continue;
            double dx = Math.Sqrt(inside);

            foreach (double x in new[] { cx - dx, cx + dx })
            {
                int px = (int)Math.Round(x);
                if (px < 0 || px >= w) continue;
                long d = ((long)y * w + px) * 4;
                buf[d] = b; buf[d + 1] = g; buf[d + 2] = r;
            }
        }

        // The same again scanned by column, which fills the near-horizontal parts of the
        // circle where one row can span many pixels.
        int clo = Math.Max(0, (int)(cx - radius) - 1), chi = Math.Min(w - 1, (int)(cx + radius) + 1);
        for (int x = clo; x <= chi; x++)
        {
            double dx = x - cx;
            double inside = radius * radius - dx * dx;
            if (inside < 0) continue;
            double dy = Math.Sqrt(inside);

            foreach (double y in new[] { cy - dy, cy + dy })
            {
                int py = (int)Math.Round(y);
                if (py < 0 || py >= h) continue;
                long d = ((long)py * w + x) * 4;
                buf[d] = b; buf[d + 1] = g; buf[d + 2] = r;
            }
        }
    }

    // ------------------------------------------------------------------ settings plumbing

    private void Queue()
    {
        if (_loading) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Push a pair of levels to both the boxes and the strip without echoing back.</summary>
    private void ApplyLevels(int black, int white, bool refresh = false)
    {
        bool was = _loading;
        _loading = true;
        black = Math.Clamp(black, 0, _maxValue - 1);
        white = Math.Clamp(white, black + 1, _maxValue);
        BlackBox.Value = black;
        WhiteBox.Value = white;
        Strip.SetLevels(black, white);
        _loading = was;
        if (refresh) Queue();
    }

    private void LevelsTyped()
    {
        if (_loading) return;
        int black = (int)(BlackBox.Value ?? 0);
        int white = (int)(WhiteBox.Value ?? _maxValue);
        if (white <= black) white = Math.Min(_maxValue, black + 1);
        ApplyLevels(black, white);
        Queue();
    }

    private void LevelsDragged()
    {
        if (_loading) return;
        _loading = true;
        BlackBox.Value = Strip.Black;
        WhiteBox.Value = Strip.White;
        _loading = false;
        Queue();
    }

    // ------------------------------------------------------------------ the wizard

    /// <summary>The wizard while it is open; the screenshot path captures it.</summary>
    public TuningWizard? Wizard { get; private set; }

    /// <summary>Flat-area changes from the wizard. There is no control for them here, so they
    /// are listed in the results and cleared by Reset.</summary>
    private List<FlatAction> _flatActions = new();

    /// <summary>The source histogram after the flat-area changes, measured at full resolution,
    /// so the figures describe the map that will actually be written.</summary>
    private long[]? _flatHist;

    /// <summary>The name the wizard offered for the saved file.</summary>
    private string? _wizardName;

    private async Task OpenWizardAsync()
    {
        if (Wizard is not null) { Wizard.Activate(); return; }
        Wizard = new TuningWizard(new WizardSource(_grey, _w, _h, _maxValue, _previewGrey, _pw, _ph, _previewScale,
                                                   _source.GreyHistogram, _fileName));
        try
        {
            await Wizard.ShowDialog(this);
            if (Wizard.Plan is { } plan)
            {
                await ApplyWizardAsync(plan);
                if (Wizard.SaveAfter) await SaveAsync();
            }
        }
        finally { Wizard = null; }
    }

    /// <summary>
    /// The wizard's answers, onto this window's own controls. Nothing is tuned here: the
    /// controls are set and the usual pass runs, so the preview, the figures and the saved file
    /// all come from <see cref="Build"/> exactly as if the boxes had been ticked by hand.
    /// </summary>
    private async Task ApplyWizardAsync(WizardPlan plan)
    {
        var o = plan.Options;
        _loading = true;
        StretchCheck.IsChecked = true;
        InvertCheck.IsChecked = false;
        SurroundCheck.IsChecked = o.UniformSurround;
        SliceCheck.IsChecked = false;
        DitherCheck.IsChecked = false;
        RimCheck.IsChecked = o.AddRim;
        if (o.AddRim)
        {
            RimBox.Value = (decimal)Math.Round(o.RimWidthMm ?? 1.0, 2);
            RampBox.Value = (decimal)Math.Round(o.RimRampMm ?? 0.0, 2);
        }
        FitCheck.IsChecked = o.Fit != FitPolicy.None;
        FitPolicyBox.SelectedIndex = o.Fit switch { FitPolicy.Canvas => 1, FitPolicy.Design => 2, _ => 0 };
        CoverRimCheck.IsChecked = o.CoverDesignRim;
        PadBox.SelectedIndex = o.PadWith == PadFill.Untouched ? 1 : 0;
        PassBox.Value = plan.Passes;
        BitBox.SelectedIndex = o.OutputBitDepth == 8 ? 1 : 0;
        DpiCheck.IsChecked = o.Dpi is not null;
        OutlineCheck.IsChecked = plan.Outline;
        _loading = false;
        ApplyLevels(plan.Black, plan.White);

        _flatActions = o.FlatActions.Where(a => a.Mode != FlatMode.Leave).ToList();
        _wizardName = plan.SuggestedName;
        _flatHist = null;
        if (_flatActions.Count > 0)
        {
            StatusText.Text = "Measuring the flat-area changes at full resolution ...";
            var actions = _flatActions;
            _flatHist = await Task.Run(() =>
            {
                var g = (ushort[])_grey.Clone();
                FlatAreas.Apply(g, _w, _h, actions);
                var hist = new long[_maxValue + 1];
                foreach (var v in g) hist[v]++;
                return hist;
            });
        }
        Recompute();

        // Recompute writes the status line; this replaces it once, so the hand-over is said.
        StatusText.Text = "The wizard's answers are set on the controls above. They are a starting point, not a lock: "
                        + "change anything you like - the preview and the figures follow. Reset puts everything back.";
    }

    private void ResetAll(int suggestedBlack, int suggestedWhite)
    {
        _flatActions = new List<FlatAction>();
        _flatHist = null;
        _wizardName = null;
        _loading = true;
        StretchCheck.IsChecked = true;
        InvertCheck.IsChecked = false;
        SurroundCheck.IsChecked = false;
        RimCheck.IsChecked = false;
        SliceCheck.IsChecked = false;
        DitherCheck.IsChecked = false;
        FitCheck.IsChecked = false;
        FitPolicyBox.SelectedIndex = 0;
        CoverRimCheck.IsChecked = false;
        PadBox.SelectedIndex = 0;
        MaskCheck.IsChecked = false;
        OutlineCheck.IsChecked = false;
        DpiCheck.IsChecked = false;
        RimBox.Value = 1.00m;
        RampBox.Value = 0.00m;
        SpotBox.Value = 7;
        PassBox.Value = _defaultPasses;
        BitBox.SelectedIndex = 0;
        _loading = false;
        ApplyLevels(suggestedBlack, suggestedWhite, refresh: true);
    }

    /// <summary>
    /// Turn the controls into a <see cref="TuningOptions"/> at full resolution.
    ///
    /// Millimetres are resolved here rather than at save time so the preview, the numbers and
    /// the written file all come from one object. A dialog whose preview is produced by
    /// different code from its output is a dialog that will eventually lie to someone.
    /// </summary>
    private TuningOptions Build()
    {
        int passes = (int)(PassBox.Value ?? 256);

        var o = new TuningOptions
        {
            BlackPoint = (int)(BlackBox.Value ?? 0),
            WhitePoint = (int)(WhiteBox.Value ?? _maxValue),
            Stretch = StretchCheck.IsChecked == true,
            Invert = InvertCheck.IsChecked == true,
            UniformSurround = SurroundCheck.IsChecked == true,
            Slices = SliceCheck.IsChecked == true ? passes : 0,
            Dither = DitherCheck.IsChecked == true,
            OutputBitDepth = BitBox.SelectedIndex == 1 ? 8 : 16,
            BlankDiameterMm = Blank.Current.DiameterMm,
            TargetDepthMm = Blank.Current.TargetDepthMm,
        };

        if (RimCheck.IsChecked == true)
        {
            o.RimWidthMm = (double)(RimBox.Value ?? 0);
            o.RimRampMm = (double)(RampBox.Value ?? 0);
            o.Fit = FitCheck.IsChecked != true ? FitPolicy.None
                  : FitPolicyBox.SelectedIndex switch
                  {
                      1 => FitPolicy.Canvas,
                      2 => FitPolicy.Design,
                      _ => FitPolicy.Content,
                  };
            o.PadWith = PadBox.SelectedIndex == 1 ? PadFill.Untouched : PadFill.Background;
            o.CoverDesignRim = CoverRimCheck.IsChecked == true && o.Fit == FitPolicy.Design;
        }

        o.FlatActions = new List<FlatAction>(_flatActions);
        o.ResolvePhysical(_w, _h);

        // ResolvePhysical turns the rim on whenever a width was given; the checkbox is the
        // authority, not the leftover number in a box the user is no longer looking at.
        if (RimCheck.IsChecked != true) o.AddRim = false;
        if (DpiCheck.IsChecked != true) o.Dpi = null;

        return o;
    }

    // ------------------------------------------------------------------ the live pass

    private void Recompute()
    {
        if (_loading) return;

        var full = Build();

        // The same settings against the smaller canvas: only the rim is in pixels, so only the
        // rim needs scaling. Levels are levels at any resolution.
        var preview = Build();
        preview.RimRadius *= _previewScale;
        preview.RimRamp *= _previewScale;

        var tuned = DepthTuner.Apply(_previewGrey, _pw, _ph, _maxValue, preview, out var rep);
        _rimReport = rep;

        // Apply resolves the rim geometry against whatever canvas it ended up with, so these
        // come back from the options rather than being worked out again here. The blank spans
        // the short side of the output, fitted or not.
        double blankRadius = full.AddRim ? Math.Min(rep.OutWidth, rep.OutHeight) / 2.0 : 0;
        TunedImage.Source = ToBitmap(tuned, rep.OutWidth, rep.OutHeight, blankRadius, preview.RimRadius);
        _tunedPreviewW = rep.OutWidth;
        _tunedPreviewH = rep.OutHeight;
        _ringBlank = blankRadius;
        _ringRim = preview.RimRadius;
        OriginalLine.ImageSize = new Size(_pw, _ph);
        TunedLine.ImageSize = new Size(rep.OutWidth, rep.OutHeight);

        // The lit pane gets the same array the flat pane was just drawn from. No second run of
        // the pipeline, and no re-reading anything from disk - which is the whole point, since
        // the round trip through a saved file is exactly what made A/B comparison painful.
        PushTunedRelief(tuned, rep.OutWidth, rep.OutHeight);

        // With flat areas changed, the levels going in are the changed map's, measured once at
        // full resolution when the wizard's answers were applied.
        _tunedHist = TuneJob.MapHistogram(_flatHist ?? _source.GreyHistogram, _maxValue, full,
                                          out long flattened, out long lifted);

        int passes = (int)(PassBox.Value ?? 256);
        var (dBefore, _) = TuneJob.DepthsAt(_source.GreyHistogram, _maxValue, passes);
        var (dAfter, _) = TuneJob.DepthsAt(_tunedHist, _maxValue, passes);
        var pBefore = TuneJob.PassesFor(_source.GreyHistogram, _maxValue, passes);
        var pAfter = TuneJob.PassesFor(_tunedHist, _maxValue, passes);
        var (tMin, tMax, tUnique) = TuneJob.Span(_tunedHist);

        double useBefore = TuneJob.RangeUse(_source.GreyHistogram, _maxValue);
        double useAfter = TuneJob.RangeUse(_tunedHist, _maxValue);

        TunedCaption.Text = $"{tUnique:N0} levels, {tMin:N0} to {tMax:N0}, "
                          + $"{useAfter * 100:F0}% of the range"
                          + (full.OutputBitDepth == 8 ? "   (written as 8-bit)" : "")
                          + StepNote(dAfter, passes)
                          + (full.AddRim
                              ? "\nCyan: edge of the blank, everything outside it is off the coin. "
                              + "Amber: where engraving stops. Neither is in the file."
                              : "");

        // Both panes terrace, because both files would be sliced at the same pass count -
        // comparing a real staircase against an ideal smooth surface would flatter the tuning.
        // What differs is how many treads each one gets, and without these two counts on the
        // pictures the honest reading of the view is "why did the original change too?".
        OriginalCaption.Text = OriginalLevelsText() + StepNote(dBefore, passes);

        // "Original" on its own means the file. Once the pane is showing a sliced surface it is
        // no longer the file, it is the job, and the header has to say so - otherwise the honest
        // baseline looks like the dialog quietly editing the thing it promised not to touch.
        bool stepped = ReliefOn && ReliefStepCheck.IsChecked == true;
        OriginalHeader.Text = stepped ? $"Original, cut at {passes:N0} passes" : "Original";
        TunedHeader.Text = stepped ? $"Tuned, cut at {passes:N0} passes" : "Tuned";

        ResultText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Distinct depths        {dBefore:N0}  to  {dAfter:N0}",
            // Relief passes, not "wasted" ones. A pass that adds no new level still fires and
            // still cuts; what it does not do is add shape. Showing the relief count makes the
            // honest point directly - the gain here is a deeper relief, not recovered
            // resolution.
            $"Passes forming relief  {pBefore.Relief:N0}  to  {pAfter.Relief:N0}",
            $"Cutting a flat recess  {pBefore.Uniform:N0}  to  {pAfter.Uniform:N0}",
            $"Range used             {useBefore * 100:F0}%  to  {useAfter * 100:F0}%",
            // "Levels absorbed", not "absorbed": the rim swallows pixels too, and lumping the
            // two together would let a rim that is eating the artwork hide inside a number the
            // reader attributes to the level points.
            $"Levels absorbed        {flattened:N0} px black, {lifted:N0} px white",
            // The one row quoted against the blank rather than the file: the target depth
            // spread over the pass count is what each pass has to remove at the deepest point,
            // the number a cut test will confirm or refute. It is not a Z advance - that has to
            // come from the removal model, because focus changes how much each pass removes.
            $"Depth per pass         {Blank.Current.TargetDepthMm / Math.Max(1, passes) * 1000:0.0} um  "
                + $"({Blank.Current.TargetDepthMm:0.00} mm over {passes:N0})",
        }.Concat(RimLines(rep, full)).Concat(FlatLines()));

        // The terrace rows arrive from the full-resolution pass; until they do, say so.
        _resultBase = ResultText.Text ?? "";
        _origCaptionBase = OriginalCaption.Text ?? "";
        _tunedCaptionBase = TunedCaption.Text ?? "";
        if (MarkOn && !ReliefOn)
        {
            ResultText.Text = _resultBase + Environment.NewLine + $"{MarkTitle(MarkMode),-22} measuring at full resolution ...";
            if (_ovOrig is { } ro) OriginalCaption.Text = _origCaptionBase + MarkCaption(ro);
        }

        UpdateStatus(full);
        StartFullPass(full);
        if (_line is { Tuned: false }) UpdateProfile();
    }

    /// <summary>
    /// The rim and fit rows of the results card.
    ///
    /// These belong with the numbers rather than beside the checkboxes that produce them. The
    /// cost of growing the canvas is the part that has to be visible without scrolling:
    /// "whole image" fits a square inside a circle, which gives up a factor of root two before
    /// the rim is even considered, and a 40 mm blank then carries about 27 mm of art. That is
    /// a fair trade for a guarantee, but only if the person making it can see the number.
    /// </summary>
    private IEnumerable<string> RimLines(TuningReport rep, TuningOptions o)
    {
        if (!o.AddRim) yield break;

        if (rep.Fit is { } fit)
        {
            // The plan is measured on the reduced preview, so quote the size as a ratio of the
            // real image rather than the preview's own pixel count, which would mean nothing.
            int fullSize = (int)Math.Round(Math.Max(_w, _h) * (fit.Size / (double)Math.Max(_pw, _ph)));
            double blank = o.BlankDiameterMm ?? 40;

            if (fit.Recentred)
            {
                yield return "Blank centred          on the design, no resampling";
                yield return $"Canvas                 {_w:N0}x{_h:N0} to ~{fullSize:N0} sq, "
                           + (fit.Crops(_pw, _ph) ? "cropped" : "padded");
                if (fit.CoveredRim is { } own)
                {
                    // Measured on the reduced preview, so to a tenth; the saved file reports its own.
                    yield return $"Design's own rim       ~{own.Width / fit.PixelsPerMm:F1} mm wide, under the new rim";
                    yield return $"Field spans            {fit.ArtAcrossMm:F1} of {blank:F0} mm, inside the ramp";
                }
                else
                {
                    if (o.CoverDesignRim)
                        yield return "Design's own rim       none found, nothing covered";
                    yield return $"Design spans           {fit.ArtAcrossMm:F1} of {blank:F0} mm, inside the rim";
                }
            }
            else
            {
                yield return $"Canvas grown           {Math.Max(_w, _h):N0} to ~{fullSize:N0} px, no resampling";
                yield return $"Artwork spans          {fit.ArtAcrossMm:F1} of {blank:F0} mm "
                           + $"({fit.ArtAcrossMm / blank * 100:F0}% of the blank)";
            }
        }
        else if (rep.FitDesignLost > 0)
        {
            yield return "Centring refused       it would crop part of the design";
        }
        else if (o.Fit != FitPolicy.None)
        {
            yield return "Canvas                 already clear, no growth needed";
        }

        yield return rep.RimClipped > 0
            ? $"Rim overlaps           {rep.RimClippedFraction * 100:F2}% of the design; "
            + $"art at {rep.SuggestedScale * 100:F0}% would clear it"
            : "Rim                    sits clear of the design";

        // The cause of a lopsided moat, said where the moat is visible. Without it the preview
        // shows the symptom - deep on one side, thin on the other - and nothing names the fix.
        if (o.Fit != FitPolicy.Design && rep.DesignOffCentreMm is >= DepthTuner.OffCentreNoteMm)
            yield return $"Design off centre      {rep.DesignOffCentreMm:F1} mm - try fit: centred on it";
    }

    /// <summary>The wizard's flat-area changes, which have no control of their own here.</summary>
    private IEnumerable<string> FlatLines()
    {
        int flat = _flatActions.Count(a => a.Mode == FlatMode.Flatten);
        int smooth = _flatActions.Count(a => a.Mode == FlatMode.Smooth);
        if (flat + smooth == 0) yield break;
        var parts = new List<string>();
        if (flat > 0) parts.Add($"{flat} flattened");
        if (smooth > 0) parts.Add($"{smooth} smoothed");
        yield return $"Flat areas             {string.Join(", ", parts)} (wizard; Reset clears)";
    }

    /// <summary>
    /// The geometry the settings imply, in the footer rather than in the settings panel.
    ///
    /// It reads as prose and it is wide, so it belongs on the wide row at the bottom; putting
    /// it in the 376px column cost three lines of height and pushed the rim controls out of
    /// sight, which is a poor trade for text nobody edits.
    /// </summary>
    private string GeometryNote(TuningOptions o)
    {
        // A fit changes how many pixels the blank spans, and with it every figure here. The
        // plan was made on the preview, so it is scaled back to the full image.
        double? fitted = _rimReport?.Fit is { } fit && _previewScale > 0 ? fit.PixelsPerMm / _previewScale : null;
        if ((fitted ?? o.PixelsPerMm(_w, _h)) is not double ppmm || ppmm <= 0) return "";

        double spot = (double)(SpotBox.Value ?? 7);
        var check = ResolutionCheck.For(1000.0 / ppmm, spot);
        string note = $"{ppmm:F1} px/mm, {check.MicronsPerPixel:F1} um/pixel against a {spot:F0} um spot "
                    + $"- {check.Note}.";

        if (o.AddRim)
        {
            double rw = o.RimWidthMm ?? 0;
            double ramp = o.RimRampMm ?? 0;
            note += $"  Rim {rw:F2} mm = {rw * ppmm:F0} px, "
                  + (ramp <= 0 ? "hard step." : $"ramp {ramp:F2} mm = {ramp * ppmm:F0} px.");

            // A ramp narrower than the beam is the worst of both worlds: the spot smears the
            // transition to its own width regardless, so the ramp buys nothing a hard step
            // would not have given, while costing depth the design could have used.
            if (ramp > 0 && ramp * 1000 < spot)
                note += $"  That ramp is {ramp * 1000:F0} um, narrower than the spot - the beam will"
                      + " smear the edge to about its own width either way.";
        }

        return note;
    }

    private void UpdateStatus(TuningOptions o)
    {
        if (o.IsNoOp(_maxValue) && !o.Stretch)
        {
            StatusText.Text = "These settings would change nothing. Move the level points, or press Suggest.";
            return;
        }

        // A fit changes the canvas, and the DPI written is recomputed for it at save time; quote
        // that figure, not the one resolved against the original canvas.
        double? fittedPpmm = _rimReport?.Fit is { } fit && _previewScale > 0 ? fit.PixelsPerMm / _previewScale : null;
        double? dpiOut = o.Dpi is double d0 ? (fittedPpmm is double fp ? fp * 25.4 : d0) : null;
        string dpi = dpiOut is double d ? $"{d:F0} dpi written in" : "no physical size written";
        string geometry = GeometryNote(o);

        StatusText.Text = $"{_w:N0} x {_h:N0}, {dpi}. Saving writes a new file; {_fileName} is never modified."
                        + (geometry.Length > 0 ? "   " + geometry : "");
    }

    // ------------------------------------------------------------------ saving

    private async Task SaveAsync()
    {
        if (_busy) return;
        var top = GetTopLevel(this);
        if (top?.StorageProvider is null) return;

        string suggested = _wizardName ?? Path.GetFileNameWithoutExtension(_fileName) + "-tuned.png";

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save tuned depth map",
            SuggestedFileName = suggested,
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } },
            SuggestedStartLocation = _sourceDir is null
                ? null
                : await top.StorageProvider.TryGetFolderFromPathAsync(_sourceDir),
        });

        if (file is null) return;

        // The status line promises the original is never modified, and the save picker will
        // happily hand back the original's own name if that is what gets clicked.
        if (_sourcePath is not null && file.TryGetLocalPath() is string target && Program.SamePath(target, _sourcePath))
        {
            StatusText.Text = $"That is the original, {_fileName}. DepthView never writes over it - choose a different name.";
            return;
        }

        _busy = true;
        SaveButton.IsEnabled = false;
        StatusText.Text = "Applying the correction at full resolution ...";

        try
        {
            var o = Build();

            // The full-resolution pass is the only expensive thing this window does, and it
            // happens once, when it has been asked for. It is also where the fit is planned
            // for real: the preview's figures come from the reduced copy, so the file gets its
            // own measurement rather than a scaled-up estimate.
            TuningReport rep = null!;
            var tuned = await Task.Run(() =>
                DepthTuner.Apply(_grey, _w, _h, _maxValue, o, out rep));

            int outW = rep.OutWidth, outH = rep.OutHeight;

            await using (var s = await file.OpenWriteAsync())
            {
                // Overwriting an existing file: the picker hands back a stream positioned at
                // zero but does not necessarily truncate, so a shorter PNG written over a
                // longer one would leave the old tail behind and produce a file that opens
                // and then fails a CRC. Truncate first where the stream allows it.
                if (s.CanSeek) s.SetLength(0);
                await Task.Run(() => TuneJob.WriteTuned(s, tuned, outW, outH, _maxValue, o, _fileName));
            }

            string? written = file.TryGetLocalPath();
            string maskNote = "";

            if (MaskCheck.IsChecked == true && o.AddRim && written is not null)
            {
                string maskPath = Path.ChangeExtension(written, null) + "-rim-mask.png";
                await Task.Run(() => TuneJob.WriteRimMask(maskPath, outW, outH, o));
                maskNote = $"  Mask written as {Path.GetFileName(maskPath)}.";
            }

            if (OutlineCheck.IsChecked == true && written is not null
                && o.BlankDiameterMm is double blankMm && blankMm > 0)
            {
                string outlinePath = Path.ChangeExtension(written, null) + "-outline.svg";
                double ppmm = Math.Min(outW, outH) / blankMm;
                double engraveMm = o.AddRim && o.RimRadius > 0 ? o.RimRadius * 2 / ppmm : 0;

                await Task.Run(() => AlignmentOutline.Write(outlinePath, blankMm, engraveMm,
                                                            Path.GetFileName(written)));
                maskNote += $"  Alignment outline written as {Path.GetFileName(outlinePath)}"
                          + " - put it on a tool layer and frame with Hull or Contour.";
            }

            StatusText.Text = $"Saved {file.Name}.{maskNote}  Re-reading it to check ...";

            string outcome = written is not null
                ? await VerifyAsync(written)
                : $"Saved {file.Name}.";
            StatusText.Text = outcome + maskNote;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Read back what was just written and analyse it as a stranger's file.
    ///
    /// The tool marking its own homework is the point. Every other number in this window is
    /// predicted from arithmetic; this one is measured from bytes on disk, and if the two ever
    /// disagree the prediction is what is wrong.
    /// </summary>
    private async Task<string> VerifyAsync(string path)
    {
        try
        {
            int passes = (int)(PassBox.Value ?? 256);
            var (name, depths, wasted, levels) = await Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(path);
                var (img, meta) = ImageLoader.Load(bytes, Path.GetFileName(path), path, "tuned");
                var a = DepthAnalyzer.Analyze(img, meta);
                var (d, w) = a.SlicesAt(passes);
                return (Path.GetFileName(path), d, w, a.UniqueGreyLevels);
            });

            return $"Saved {name}. Read back from disk: {levels:N0} grey levels, "
                 + $"{depths:N0} distinct depths at {passes:N0} passes, {wasted:N0} passes repeating one.";
        }
        catch (Exception ex)
        {
            return $"Saved {Path.GetFileName(path)}, but reading it back failed: {ex.Message}";
        }
    }
}
