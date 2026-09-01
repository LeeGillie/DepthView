using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepthView.Analysis;
using DepthView.Imaging;
using DepthView.Processing;

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

    private const int PreviewEdge = 560;

    public TuneWindow(ImageData image, AnalysisResult result, string fileName, string? sourcePath)
    {
        InitializeComponent();

        _image = image;
        _source = result;
        _fileName = fileName;
        _sourceDir = sourcePath is null ? null : Path.GetDirectoryName(sourcePath);

        _w = image.Width;
        _h = image.Height;
        _maxValue = image.MaxValue;
        _grey = DepthTuner.ExtractGrey(image);

        BuildPreviewSource();

        SourceText.Text = $"{fileName}   {_w:N0} x {_h:N0}   {image.BitDepth}-bit";

        BlackBox.Maximum = _maxValue;
        WhiteBox.Maximum = _maxValue;

        Strip.SetData(result.GreyHistogram, _maxValue);

        var (sb, sw) = DepthTuner.SuggestLevels(result.GreyHistogram);
        ApplyLevels(sb, sw);

        BlackBox.ValueChanged += (_, _) => LevelsTyped();
        WhiteBox.ValueChanged += (_, _) => LevelsTyped();
        Strip.LevelsChanged += (_, _) => LevelsDragged();

        StretchCheck.IsCheckedChanged += (_, _) => Queue();
        InvertCheck.IsCheckedChanged += (_, _) => Queue();
        RimCheck.IsCheckedChanged += (_, _) => Queue();
        FitCheck.IsCheckedChanged += (_, _) => Queue();
        FitPolicyBox.SelectionChanged += (_, _) => Queue();
        PadBox.SelectionChanged += (_, _) => Queue();
        SliceCheck.IsCheckedChanged += (_, _) => Queue();
        DitherCheck.IsCheckedChanged += (_, _) => Queue();
        MaskCheck.IsCheckedChanged += (_, _) => Queue();
        OutlineCheck.IsCheckedChanged += (_, _) => Queue();
        DpiCheck.IsCheckedChanged += (_, _) => Queue();

        BlankBox.ValueChanged += (_, _) => Queue();
        RimBox.ValueChanged += (_, _) => Queue();
        RampBox.ValueChanged += (_, _) => Queue();
        SpotBox.ValueChanged += (_, _) => Queue();
        PassBox.ValueChanged += (_, _) => Queue();
        BitBox.SelectionChanged += (_, _) => Queue();

        StripLogCheck.IsCheckedChanged += (_, _) => Strip.LogScale = StripLogCheck.IsChecked == true;

        SuggestButton.Click += (_, _) => ApplyLevels(sb, sw, refresh: true);
        FullRangeButton.Click += (_, _) => ApplyLevels(0, _maxValue, refresh: true);
        UsedRangeButton.Click += (_, _) => ApplyLevels(result.MinLevel, result.MaxLevel, refresh: true);
        ResetButton.Click += (_, _) => ResetAll(sb, sw);
        SaveButton.Click += async (_, _) => await SaveAsync();
        CloseButton.Click += (_, _) => Close();

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
        if (Program.StartupBlankMm is double blank && blank > 0) BlankBox.Value = (decimal)blank;
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
            FitPolicyBox.SelectedIndex = Program.StartupFit == FitPolicy.Canvas ? 1 : 0;
        }
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

        var (min, max, unique) = TuneJob.Span(_source.GreyHistogram);
        OriginalCaption.Text = $"{unique:N0} levels, {min:N0} to {max:N0}, "
                             + $"{TuneJob.RangeUse(_source.GreyHistogram, _maxValue) * 100:F0}% of the range";
    }

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

    private void ResetAll(int suggestedBlack, int suggestedWhite)
    {
        _loading = true;
        StretchCheck.IsChecked = true;
        InvertCheck.IsChecked = false;
        RimCheck.IsChecked = false;
        SliceCheck.IsChecked = false;
        DitherCheck.IsChecked = false;
        FitCheck.IsChecked = false;
        FitPolicyBox.SelectedIndex = 0;
        PadBox.SelectedIndex = 0;
        MaskCheck.IsChecked = false;
        OutlineCheck.IsChecked = false;
        DpiCheck.IsChecked = false;
        BlankBox.Value = 40;
        RimBox.Value = 1.00m;
        RampBox.Value = 0.00m;
        SpotBox.Value = 7;
        PassBox.Value = 256;
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
            Slices = SliceCheck.IsChecked == true ? passes : 0,
            Dither = DitherCheck.IsChecked == true,
            OutputBitDepth = BitBox.SelectedIndex == 1 ? 8 : 16,
            BlankDiameterMm = (double)(BlankBox.Value ?? 40),
        };

        if (RimCheck.IsChecked == true)
        {
            o.RimWidthMm = (double)(RimBox.Value ?? 0);
            o.RimRampMm = (double)(RampBox.Value ?? 0);
            o.Fit = FitCheck.IsChecked != true ? FitPolicy.None
                  : FitPolicyBox.SelectedIndex == 1 ? FitPolicy.Canvas
                  : FitPolicy.Content;
            o.PadWith = PadBox.SelectedIndex == 1 ? PadFill.Untouched : PadFill.Background;
        }

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

        _tunedHist = TuneJob.MapHistogram(_source.GreyHistogram, _maxValue, full,
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
                          + (full.AddRim
                              ? "\nCyan: edge of the blank, everything outside it is off the coin. "
                              + "Amber: where engraving stops. Neither is in the file."
                              : "");

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
        }.Concat(RimLines(rep, full)));

        UpdateStatus(full);
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

            yield return $"Canvas grown           {Math.Max(_w, _h):N0} to ~{fullSize:N0} px, no resampling";
            yield return $"Artwork spans          {fit.ArtAcrossMm:F1} of {blank:F0} mm "
                       + $"({fit.ArtAcrossMm / blank * 100:F0}% of the blank)";
        }
        else if (o.Fit != FitPolicy.None)
        {
            yield return "Canvas                 already clear, no growth needed";
        }

        yield return rep.RimClipped > 0
            ? $"Rim overlaps           {rep.RimClippedFraction * 100:F2}% of the design; "
            + $"art at {rep.SuggestedScale * 100:F0}% would clear it"
            : "Rim                    sits clear of the design";
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
        if (o.PixelsPerMm(_w, _h) is not double ppmm || ppmm <= 0) return "";

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

        string dpi = o.Dpi is double d ? $"{d:F0} dpi written in" : "no physical size written";
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

        string suggested = Path.GetFileNameWithoutExtension(_fileName) + "-tuned.png";

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
