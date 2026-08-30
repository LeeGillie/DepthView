using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepthView.Analysis;
using DepthView.Imaging;

namespace DepthView.Views;

public partial class MainWindow : Window
{
    private ImageData? _image;
    private ImageMetadata? _meta;
    private AnalysisResult? _result;
    private string? _lastPath;
    private bool _busy;
    private ReliefWindow? _relief;

    /// <summary>Only set on the --about screenshot path; the About button uses a dialog.</summary>
    private AboutWindow? _about;

    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xD0, 0x8C));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xA6, 0xD8));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B));
    private static readonly IBrush AlertBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x7A, 0x6E));
    private static readonly IBrush ValueBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E));

    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DropZone.PointerPressed += async (_, _) => await BrowseAsync();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        PasteButton.Click += async (_, _) => await PasteAsync();
        ReloadButton.Click += async (_, _) => await ReloadAsync();
        CopyButton.Click += async (_, _) => await CopyReportAsync();
        SaveButton.Click += async (_, _) => await SaveReportAsync();
        ReliefButton.Click += (_, _) => OpenRelief();
        ClearButton.Click += (_, _) => ClearAll();
        AboutButton.Click += (_, _) => new AboutWindow().ShowDialog(this);

        PreviewMode.SelectionChanged += (_, _) => UpdatePreview();
        LogCheck.IsCheckedChanged += (_, _) => Histogram.LogScale = LogCheck.IsChecked == true;
        ResetZoomButton.Click += (_, _) => { Histogram.ResetZoom(); UpdateRangeText(); };
        ZoomUsedButton.Click += (_, _) => ZoomToUsed();

        Histogram.HoverTextChanged += (_, text) =>
            StatusText.Text = text ?? DefaultStatus();
        Histogram.PointerMoved += (_, _) => UpdateRangeText();
        Histogram.PointerWheelChanged += (_, _) => UpdateRangeText();

        ShowIntroDetails();

        Opened += async (_, _) =>
        {
            if (Program.StartupAbout)
            {
                // Show, not ShowDialog: a modal loop would own the dispatcher and the
                // screenshot timer below would never get a turn.
                _about = new AboutWindow();
                _about.Show(this);
                if (Program.ScreenshotPath is not null) ScheduleScreenshot();
                return;
            }

            var start = Program.StartupFile;
            if (string.IsNullOrEmpty(start) || !File.Exists(start)) return;
            var bytes = await File.ReadAllBytesAsync(start);
            await LoadBytesAsync(bytes, Path.GetFileName(start), start, "opened from the command line");
            if (Program.StartupRelief) OpenRelief();
            if (Program.ScreenshotPath is not null) ScheduleScreenshot();
        };
    }

    /// <summary>
    /// Captures the real window to a PNG and exits, so documentation screenshots are
    /// reproducible from a command line rather than hand-grabbed and gradually going stale.
    /// The delay lets the analysis, the histogram and the relief render all settle first.
    /// </summary>
    private void ScheduleScreenshot()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(
                Program.ScreenshotDelayMs
                ?? (Program.StartupRelief ? 4500 : Program.StartupAbout ? 1400 : 2200))
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Histogram.ClearHover();
                Window target =
                    Program.StartupAbout && _about is not null ? _about :
                    Program.StartupRelief && _relief is not null ? _relief : this;
                var size = new PixelSize(Math.Max(1, (int)target.ClientSize.Width),
                                         Math.Max(1, (int)target.ClientSize.Height));

                using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
                rtb.Render(target);
                using var fs = File.Create(Program.ScreenshotPath!);
                rtb.Save(fs);
                Console.WriteLine($"screenshot {size.Width}x{size.Height} -> {Program.ScreenshotPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Screenshot failed: " + ex.Message);
            }

            (Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };

        timer.Start();
    }

    // ------------------------------------------------------------------ input

    // Avalonia 11.3 marks DataFormats / IClipboard.GetDataAsync obsolete in favour of the
    // DataTransfer API arriving in 12.x. The old API is the one that works across the whole
    // 11.x line, so we stay on it deliberately rather than pinning to a preview surface.
#pragma warning disable CS0618

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var file = e.Data.GetFiles()?.OfType<IStorageFile>().FirstOrDefault();
        if (file is null)
        {
            ShowError("That drop did not contain a file. Drag an image file from your file manager.");
            return;
        }
        await LoadStorageFileAsync(file, "dropped onto the window");
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.V) { e.Handled = true; await PasteAsync(); }
        else if (ctrl && e.Key == Key.O) { e.Handled = true; await BrowseAsync(); }
        else if (e.Key == Key.F5) { e.Handled = true; await ReloadAsync(); }
    }

    private async Task BrowseAsync()
    {
        if (_busy) return;
        var top = GetTopLevel(this);
        if (top?.StorageProvider is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a depth map candidate",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Depth map images")
                {
                    Patterns = ImageLoader.SupportedPatterns.Split(';')
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file is not null) await LoadStorageFileAsync(file, "opened from disk");
    }

    private async Task ReloadAsync()
    {
        if (_busy || string.IsNullOrEmpty(_lastPath) || !File.Exists(_lastPath)) return;
        var bytes = await File.ReadAllBytesAsync(_lastPath);
        await LoadBytesAsync(bytes, Path.GetFileName(_lastPath), _lastPath, "re-read from disk");
    }

    private async Task PasteAsync()
    {
        if (_busy) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { ShowError("No clipboard is available on this platform."); return; }

        string[] formats;
        try { formats = await clipboard.GetFormatsAsync(); }
        catch (Exception ex) { ShowError("Could not read the clipboard: " + ex.Message); return; }

        // Best case: the clipboard holds a reference to a real file, so we can read exact bytes.
        if (formats.Contains(DataFormats.Files))
        {
            var data = await clipboard.GetDataAsync(DataFormats.Files);
            var file = (data as IEnumerable<IStorageItem>)?.OfType<IStorageFile>().FirstOrDefault();
            if (file is not null)
            {
                await LoadStorageFileAsync(file, "pasted as a file reference");
                return;
            }
        }

        // Fallback: a raw encoded bitmap on the clipboard.
        foreach (var fmt in new[] { "PNG", "image/png", "public.png", "image/tiff", "TIFF", "image/bmp" })
        {
            if (!formats.Contains(fmt)) continue;
            object? o;
            try { o = await clipboard.GetDataAsync(fmt); } catch { continue; }

            byte[]? bytes = o as byte[] ?? (o as MemoryStream)?.ToArray();
            if (bytes is null || bytes.Length < 16) continue;

            await LoadBytesAsync(bytes, $"clipboard.{fmt.Split('/').Last().ToLowerInvariant()}", null,
                "pasted as a clipboard bitmap");

            if (_meta is not null)
                _meta.Warnings.Add(
                    "This image came from the clipboard as a bitmap, not as a file. Most applications " +
                    "and every operating system clipboard bitmap path reduce images to 8 bits per " +
                    "channel, so a 16-bit source may already have been flattened before DepthView saw " +
                    "it. Drop or browse to the original file for a trustworthy bit-depth reading.");

            return;
        }

        ShowError(
            "The clipboard does not hold an image DepthView can read. Copy the file itself in your file " +
            "manager (which pastes a file reference and preserves exact bytes) rather than copying the " +
            "picture out of a viewer. Formats currently on the clipboard: " +
            (formats.Length == 0 ? "none" : string.Join(", ", formats.Take(12))));
    }

#pragma warning restore CS0618

    private async Task LoadStorageFileAsync(IStorageFile file, string source)
    {
        try
        {
            string? path = file.TryGetLocalPath();
            byte[] bytes;

            if (path is not null && File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                await using var s = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            await LoadBytesAsync(bytes, file.Name, path, source);
        }
        catch (Exception ex)
        {
            ShowError("Could not read that file: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ pipeline

    private async Task LoadBytesAsync(byte[] bytes, string? name, string? path, string source)
    {
        _busy = true;
        ErrorText.Text = "";
        StatusText.Text = $"Decoding {name ?? "image"} ...";

        try
        {
            var (img, meta) = await Task.Run(() => ImageLoader.Load(bytes, name, path, source));
            var result = await Task.Run(() => DepthAnalyzer.Analyze(img, meta));

            _image = img;
            _meta = meta;
            _result = result;
            _lastPath = path;

            DropHint.IsVisible = false;
            UpdatePreview();
            ShowVerdict(result);
            PopulateDetails(result);

            Histogram.LogScale = LogCheck.IsChecked == true;
            Histogram.SetData(result.GreyHistogram);
            UpdateRangeText();

            ReloadButton.IsEnabled = path is not null;
            CopyButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
            ReliefButton.IsEnabled = true;
            ZoomUsedButton.IsEnabled = true;
            ResetZoomButton.IsEnabled = true;

            TimingText.Text = $"analysed in {result.Elapsed.TotalMilliseconds:N0} ms";
            StatusText.Text = DefaultStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ClearAll()
    {
        _image = null; _meta = null; _result = null; _lastPath = null;
        Thumb.Source = null;
        DropHint.IsVisible = true;
        VerdictCard.IsVisible = false;
        ErrorText.Text = "";
        TimingText.Text = "";
        Histogram.Clear();
        RangeText.Text = "";
        ReloadButton.IsEnabled = CopyButton.IsEnabled = SaveButton.IsEnabled =
            ClearButton.IsEnabled = ReliefButton.IsEnabled =
            ZoomUsedButton.IsEnabled = ResetZoomButton.IsEnabled = false;
        _relief?.Close();
        _relief = null;
        ShowIntroDetails();
        StatusText.Text = "Ready. Drop an image on the panel at left, click it to browse, or press Ctrl+V.";
    }

    /// <summary>
    /// A raw grey ramp tells you almost nothing about how a relief will actually look, so this
    /// opens a lit preview. Reopening replaces the window rather than stacking copies.
    /// </summary>
    private void OpenRelief()
    {
        if (_image is null) return;

        _relief?.Close();
        _relief = new ReliefWindow(_image, _meta?.FileName ?? "image");
        _relief.Closed += (_, _) => _relief = null;
        _relief.Show(this);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        StatusText.Text = "Load failed.";
    }

    private string DefaultStatus()
    {
        if (_result is null) return "Ready.";
        return $"{_result.UniqueGreyLevels:N0} unique grey levels  |  " +
               $"{_result.NonGreyPixels:N0} non-grey pixels  |  " +
               "hover the histogram for exact per-level counts, wheel to zoom, right-click to reset.";
    }

    private void UpdateRangeText()
    {
        if (_result is null) { RangeText.Text = ""; return; }
        var (lo, hi) = Histogram.Range;
        RangeText.Text = $"showing levels {lo:N0} - {hi:N0} of 0 - {_result.MaxValue:N0}";
    }

    private void ZoomToUsed()
    {
        if (_result is null || _result.UniqueGreyLevels == 0) return;
        int pad = Math.Max(1, (_result.MaxLevel - _result.MinLevel) / 40);
        Histogram.SetRange(Math.Max(0, _result.MinLevel - pad), Math.Min(_result.MaxValue, _result.MaxLevel + pad));
        UpdateRangeText();
    }

    // ------------------------------------------------------------------ preview

    private void UpdatePreview()
    {
        if (_image is null) return;

        int mode = PreviewMode.SelectedIndex;
        var img = _image;

        const int MaxEdge = 760;
        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(img.Width, img.Height));
        int pw = Math.Max(1, (int)Math.Round(img.Width * scale));
        int ph = Math.Max(1, (int)Math.Round(img.Height * scale));

        var buf = new byte[pw * ph * 4];

        double lo = _result?.MinLevel ?? 0;
        double hi = _result?.MaxLevel ?? img.MaxValue;
        if (img.Kind == SampleKind.Float) { lo = _result?.FloatMin ?? 0; hi = _result?.FloatMax ?? 1; }
        double range = hi - lo;
        if (range <= 0) range = 1;

        int ch = img.Channels;

        for (int y = 0; y < ph; y++)
        {
            int sy = (int)((long)y * img.Height / ph);
            for (int x = 0; x < pw; x++)
            {
                int sx = (int)((long)x * img.Width / pw);
                long o = ((long)sy * img.Width + sx) * ch;
                int d = (y * pw + x) * 4;

                byte r, g, b;

                if (img.Kind == SampleKind.Float)
                {
                    float v = img.Floats![o];
                    double norm = mode == 1 ? (v - lo) / range : Math.Clamp(v, 0, 1);
                    byte gv = (byte)Math.Clamp(norm * 255.0, 0, 255);
                    r = g = b = gv;
                }
                else
                {
                    var s = img.Samples!;
                    int sr = s[o];
                    int sg = ch >= 3 ? s[o + 1] : sr;
                    int sb = ch >= 3 ? s[o + 2] : sr;

                    switch (mode)
                    {
                        case 1: // auto-stretch on the grey ramp
                        {
                            byte f(int v) => (byte)Math.Clamp((v - lo) / range * 255.0, 0, 255);
                            r = f(sr); g = f(sg); b = f(sb);
                            break;
                        }
                        case 2: // low byte only
                        {
                            if (img.BitDepth <= 8) { r = g = b = 0; }
                            else { r = (byte)(sr & 0xFF); g = (byte)(sg & 0xFF); b = (byte)(sb & 0xFF); }
                            break;
                        }
                        case 3: // colour mask
                        {
                            byte grey = Scale(sr, img.MaxValue);
                            if (ch >= 3 && !(sr == sg && sg == sb))
                            {
                                r = 235; g = (byte)(grey / 4); b = (byte)(grey / 4);
                            }
                            else
                            {
                                byte dim = (byte)(grey / 2 + 20);
                                r = g = b = dim;
                            }
                            break;
                        }
                        default:
                            r = Scale(sr, img.MaxValue);
                            g = Scale(sg, img.MaxValue);
                            b = Scale(sb, img.MaxValue);
                            break;
                    }
                }

                buf[d] = b;
                buf[d + 1] = g;
                buf[d + 2] = r;
                buf[d + 3] = 255;
            }
        }

        var bmp = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96),
            PixelFormats.Bgra8888, AlphaFormat.Opaque);

        using (var fb = bmp.Lock())
        {
            int rowBytes = pw * 4;
            for (int y = 0; y < ph; y++)
                Marshal.Copy(buf, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }

        Thumb.Source = bmp;
    }

    private static byte Scale(int v, int max)
        => max <= 0 ? (byte)0 : (byte)Math.Clamp((long)v * 255 / max, 0, 255);

    // ------------------------------------------------------------------ verdict + details

    private void ShowVerdict(AnalysisResult r)
    {
        VerdictCard.IsVisible = true;
        VerdictTitle.Text = r.Verdict;
        VerdictBody.Text = r.VerdictDetail;
        VerdictTitle.Foreground = BrushFor(r.VerdictSeverity);
        VerdictCard.BorderBrush = BrushFor(r.VerdictSeverity);
    }

    private static IBrush BrushFor(Severity s) => s switch
    {
        Severity.Good => GoodBrush,
        Severity.Warn => WarnBrush,
        Severity.Alert => AlertBrush,
        _ => InfoBrush
    };

    private void ShowIntroDetails()
    {
        DetailsPanel.Children.Clear();
        Section("What DepthView measures", "A short orientation. Load a file to replace this with real numbers.");

        Para("Container vs content",
            "The top half of this panel reports what the file header declares. The bottom half reports " +
            "what the pixels actually contain. A depth map is only as good as the smaller of the two.");

        Para("Unique grey levels",
            "Counted as distinct values where R, G and B are all equal. A true 16-bit depth map should " +
            "show thousands to tens of thousands of them. Exactly 256 or fewer in a 16-bit file is the " +
            "classic imposter signature.");

        Para("Imposters",
            "An 8-bit depth map re-saved as 16-bit usually leaves fingerprints: every value becomes " +
            "v x 257 (byte replication) or v x 256 (left shift), or the levels land on a perfectly even " +
            "ladder. DepthView tests for all three and also reports any other uniform quantisation step, " +
            "so a 10-bit or 12-bit map hiding in a 16-bit file is caught too.");

        Para("Non-greyscale pixels",
            "Counted per pixel and per distinct colour. Anything above zero in a depth map usually means " +
            "JPEG chroma damage, a colourised preview, or a turbo/viridis encoded depth image rather " +
            "than a real grey ramp.");

        Para("Exact decoding",
            "PNG, PGM/PPM/PBM and PFM are decoded by DepthView itself precisely so 16-bit samples are " +
            "never quietly reduced to 8 bits on the way in, which is what every standard platform " +
            "imaging stack would do. Other formats go through ImageSharp and are flagged when their " +
            "precision cannot be guaranteed.");
    }

    private void PopulateDetails(AnalysisResult r)
    {
        DetailsPanel.Children.Clear();
        var m = r.Meta;

        // ---- file ----
        Section("File", "Where this image came from and how big it is on disk.");
        Row("Name", m.FileName ?? "(unnamed)", "The file name as reported by the source.");
        if (!string.IsNullOrEmpty(m.FilePath))
            Row("Path", m.FilePath!, "Full path on disk. Present only when DepthView could read the original file.");
        Row("Size on disk", FormatBytes(m.FileBytes),
            "Compare this with the raw sample size below. An imposter costs double the bytes for no extra information.");
        Row("Raw sample size", FormatBytes(RawBytes(r)),
            "Width x height x channels x bit depth. This is what the pixels would occupy uncompressed.");
        Row("Loaded via", m.SourceNote ?? "unknown",
            "Dropped and browsed files are read byte-exact. Clipboard bitmaps usually are not.");

        // ---- container ----
        Section("Container (what the file declares)",
            "Read straight from the file header, before any pixel is examined.");
        Row("Format", m.Format, "The container format DepthView detected from the file's magic bytes.");
        Row("Colour model", m.ColorModel, "How the format says the samples are organised.");
        Row("Declared bit depth", $"{m.DeclaredBitDepth} bits/sample",
            "Bits per sample the header claims. The measured level count further down is the reality check on this number.",
            m.DeclaredBitDepth >= 16 ? InfoBrush : ValueBrush);
        Row("Declared channels", m.DeclaredChannels.ToString(),
            "Samples per pixel according to the header.");
        Row("Bit depth is exact", m.BitDepthIsExact ? "yes - decoded natively" : "no - decoder normalised samples",
            "When this says no, imposter detection is unreliable because the decoder may have changed the sample values.",
            m.BitDepthIsExact ? GoodBrush : WarnBrush);
        Row("Alpha channel", m.HasAlpha ? "present" : "none", "Whether the container carries an alpha channel.");
        if (m.IsPalette) Row("Palette entries", m.PaletteSize.ToString(),
            "Indexed images store colours in a lookup table. DepthView expands them before analysis.");
        Row("Compression", m.CompressionMethod ?? "unknown", "The compression scheme declared by the file.");
        if (m.FilterMethod is not null)
            Row("Filtering", m.FilterMethod, "PNG applies a per-scanline predictor before compressing.");
        Row("Interlacing", m.InterlaceMethod ?? "unknown", "Interlaced files are decoded correctly but are larger and slower to read.");
        if (m.Gamma is { } g) Row("Gamma", g.ToString("F5", CultureInfo.InvariantCulture),
            "Depth maps should normally be linear (1.0). A display gamma here will distort the depth ramp in viewers.",
            Math.Abs(g - 1.0) < 0.001 ? ValueBrush : WarnBrush);
        if (m.SignificantBits is { Length: > 0 })
            Row("Significant bits (sBIT)", string.Join(", ", m.SignificantBits),
                "The PNG sBIT chunk declares how many bits per channel are actually meaningful. Compare with effective bits below.");
        Row("ICC profile", m.HasIccProfile ? m.IccProfileName ?? "embedded" : "none",
            "Colour management applied to depth values will alter them. Depth maps are usually best stored with no profile.",
            m.HasIccProfile ? WarnBrush : ValueBrush);
        if (m.DpiX is { } dx) Row("Resolution", $"{dx:F1} x {m.DpiY:F1} DPI", "Physical resolution hint. Irrelevant to depth accuracy.");

        // ---- measured ----
        Section("Content (what the pixels actually contain)",
            "Measured by walking every pixel in the image.");
        Row("Dimensions", r.DimensionText, "Width x height and total pixel count.");
        Row("Channels analysed", r.Channels.ToString(),
            "How many samples per pixel DepthView examined after decoding.");
        Row("Sample range", r.IsFloat
                ? $"{r.FloatMin:G6} .. {r.FloatMax:G6} (float32)"
                : $"0 .. {r.MaxValue:N0}",
            "The full value range the container can represent.");

        Row("Unique grey levels", r.UniqueGreyLevels.ToString("N0"),
            "Distinct values where R = G = B. This is the single most important number on this screen: " +
            "it is the real information content of the depth map, regardless of what the header claims.",
            LevelBrush(r));

        Row("Greyscale pixels", $"{r.GreyPixels:N0} ({Pct(r.GreyPixels, r.PixelCount)})",
            "Pixels where all colour channels are identical.");
        Row("Non-greyscale pixels", $"{r.NonGreyPixels:N0} ({Pct(r.NonGreyPixels, r.PixelCount)})",
            "Pixels where R, G and B are not all equal. A clean depth map should report zero here.",
            r.NonGreyPixels == 0 ? GoodBrush : WarnBrush);
        Row("Unique non-grey colours",
            $"{(r.NonGreyColorsCapped ? "over " : "")}{r.UniqueNonGreyColors:N0}",
            "How many distinct non-neutral RGB triples occur. A handful suggests compression artefacts; " +
            "thousands suggest the image is a colour-encoded depth map, not a grey one.");
        if (r.UniqueColorsTotal > 0)
            Row("Unique colours (all)", $"{(r.TotalColorsCapped ? "over " : "")}{r.UniqueColorsTotal:N0}",
                "Distinct RGB triples including the neutral ones.");
        if (r.HistR is not null)
            Row("Unique per channel", $"R {r.UniqueR:N0}   G {r.UniqueG:N0}   B {r.UniqueB:N0}",
                "Distinct values in each channel taken separately. In a true greyscale-as-RGB file these three match exactly.");

        // ---- structure ----
        Section("Level structure", "The shape of the grey ramp, which is where imposters give themselves away.");
        Row("Occupied range", $"{r.MinLevel:N0} .. {r.MaxLevel:N0}",
            "Lowest and highest grey level that occurs at least once.");
        Row("Range utilisation", $"{r.RangeUtilisation * 100:F2}%",
            "How much of the container's range the data spans. Low values mean wasted precision.",
            r.RangeUtilisation < 0.6 ? WarnBrush : ValueBrush);
        Row("Level occupancy", $"{r.Occupancy * 100:F4}%",
            "Unique levels divided by total possible levels. A true 16-bit map with fine gradients " +
            "occupies a meaningful fraction; 0.39% means exactly 256 of 65,536.",
            r.Occupancy < 0.01 ? WarnBrush : ValueBrush);
        Row("Effective bits", $"{r.EffectiveBits} bits",
            "log2 of the unique level count, rounded up. The real information depth of this file.",
            r.EffectiveBits >= r.BitDepth - 1 ? GoodBrush : WarnBrush);
        Row("Level step (GCD)", r.LevelStep.ToString("N0"),
            "The greatest common divisor of the gaps between consecutive used levels. A step of 1 is " +
            "what native data looks like. A step of 257 is byte replication; 256 is a left shift; any " +
            "other value above 1 means the data was quantised to fewer bits then stretched.",
            r.LevelStep > 1 ? AlertBrush : ValueBrush);
        Row("Uniform ladder", r.UniformLadder ? "YES - levels are perfectly evenly spaced" : "no",
            "True when every used level sits exactly on the same step. Real depth data essentially never does this.",
            r.UniformLadder ? AlertBrush : GoodBrush);
        Row("Histogram gaps", $"{r.GapCount:N0} gaps, largest {r.LargestGap:N0} levels",
            "Empty stretches inside the occupied range. A regular comb of gaps is the visual form of the level step above.");
        Row("Mean / median", $"{r.Mean:F2} / {r.Median:F0}", "Central tendency of the grey ramp.");
        Row("Std deviation", r.StdDev.ToString("F2"), "Spread of grey levels. Very low values mean a nearly flat map.");
        Row("1st / 99th percentile", $"{r.P1:N0} / {r.P99:N0}",
            "Where the bulk of the data sits, ignoring outliers at each end.");

        if (r.HasAlphaChannel)
            Row("Alpha range", r.AlphaConstant ? $"constant {r.AlphaMin:N0}" : $"{r.AlphaMin:N0} .. {r.AlphaMax:N0}",
                "Some depth pipelines hide a validity or confidence mask in the alpha channel.",
                r.AlphaConstant ? ValueBrush : WarnBrush);

        // ---- endpoints ----
        Section("Endpoints and clipping",
            "The two ends of the ramp. In a laser workflow pure white is untouched surface and " +
            "pure black is full depth, so these counts say how much of the piece is left alone " +
            "and how much is bottomed out.");

        Row($"Pure white (level {r.MaxValue:N0})",
            r.PureWhitePixels > 0
                ? $"{r.PureWhitePixels:N0} px  ({Pct(r.PureWhitePixels, r.GreyPixels)})"
                : "none present",
            "Pixels sitting exactly on the container maximum. These receive zero laser passes and " +
            "are left as bare surface. A large flat count can also mean clipped highlights.",
            r.PureWhitePixels == 0 ? LabelBrush
                : 100.0 * r.PureWhitePixels / Math.Max(1, r.GreyPixels) > 1 ? WarnBrush : ValueBrush);

        Row("Lightest level present",
            $"{r.LightestLevel:N0}   {r.LightestCount:N0} px  ({Pct(r.LightestCount, r.GreyPixels)})",
            r.PureWhitePixels > 0
                ? "The highest value that occurs. Same as pure white here, because the map reaches its ceiling."
                : $"The map never reaches white, so this is as light as it gets. {r.HeadroomTop:N0} levels " +
                  "above it are unused, and every pixel in the image will be engraved to some degree.");

        Row("Pure black (level 0)",
            r.PureBlackPixels > 0
                ? $"{r.PureBlackPixels:N0} px  ({Pct(r.PureBlackPixels, r.GreyPixels)})"
                : "none present",
            "Pixels sitting exactly on zero, the deepest point. A large flat count usually means the " +
            "shadows were clipped and relief detail past that depth no longer exists.",
            r.PureBlackPixels == 0 ? LabelBrush
                : 100.0 * r.PureBlackPixels / Math.Max(1, r.GreyPixels) > 1 ? WarnBrush : ValueBrush);

        Row("Darkest level present",
            $"{r.DarkestLevel:N0}   {r.DarkestCount:N0} px  ({Pct(r.DarkestCount, r.GreyPixels)})",
            r.PureBlackPixels > 0
                ? "The lowest value that occurs. Same as pure black here, because the map reaches its floor."
                : $"The map never reaches black, so this is as deep as it gets. {r.HeadroomBottom:N0} levels " +
                  "below it are unused. Rescaling to reach 0 would spend the full depth budget.");

        Row("Unused headroom",
            $"{r.HeadroomTop:N0} above   {r.HeadroomBottom:N0} below",
            "Levels wasted at each end. Every unused level at the top is a laser pass that does nothing, " +
            "and every unused level at the bottom is depth you paid for and did not use.",
            r.HeadroomTop + r.HeadroomBottom > (r.MaxValue + 1) / 4 ? WarnBrush : ValueBrush);

        // ---- top levels ----
        if (r.TopLevels.Count > 0)
        {
            Section("Most common levels", "The eight grey levels with the highest pixel counts.");
            foreach (var (level, count) in r.TopLevels)
                Row($"Level {level:N0}", $"{count:N0} px  ({Pct(count, r.GreyPixels)})",
                    $"Level {level:N0} occupies {Pct(count, r.GreyPixels)} of all greyscale pixels. " +
                    "A single level dominating usually means a flat background or a clipped far plane.");
        }

        // ---- findings ----
        if (r.Findings.Count > 0)
        {
            Section("Findings", "Everything worth flagging, ordered from most to least serious.");
            foreach (var f in r.Findings.OrderBy(f => FindingOrder(f.Severity)))
                AddFinding(f);
        }

        // ---- embedded text ----
        if (m.Text.Count > 0)
        {
            Section("Embedded metadata", "Text chunks and EXIF tags found in the file. Often names the tool that made it.");
            foreach (var kv in m.Text.Take(40))
                Row(kv.Key, Truncate(kv.Value, 300), $"Metadata entry '{kv.Key}' stored inside the file.");
        }
    }

    private static int FindingOrder(Severity s) => s switch
    {
        Severity.Alert => 0, Severity.Warn => 1, Severity.Info => 2, _ => 3
    };

    private IBrush LevelBrush(AnalysisResult r)
    {
        if (r.IsFloat) return ValueBrush;
        if (r.BitDepth >= 16 && r.UniqueGreyLevels <= 256) return AlertBrush;
        if (r.UniqueGreyLevels < (r.MaxValue + 1) / 4) return WarnBrush;
        return GoodBrush;
    }

    // ------------------------------------------------------------------ panel builders

    private void Section(string title, string tip)
    {
        var tb = new TextBlock { Text = title };
        tb.Classes.Add("section");
        ToolTip.SetTip(tb, tip);
        DetailsPanel.Children.Add(tb);

        DetailsPanel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x2A, 0x32)),
            Margin = new Thickness(0, 0, 0, 6)
        });
    }

    private void Row(string label, string value, string tip, IBrush? valueBrush = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("196,*"),
            Margin = new Thickness(0, 2, 0, 2)
        };

        var l = new TextBlock { Text = label, Foreground = LabelBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var v = new TextBlock
        {
            Text = value,
            Foreground = valueBrush ?? ValueBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
        };

        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);

        ToolTip.SetTip(grid, tip);
        ToolTip.SetShowDelay(grid, 350);
        DetailsPanel.Children.Add(grid);
    }

    private void Para(string title, string body)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 8), Spacing = 3 };
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = ValueBrush
        });
        sp.Children.Add(new TextBlock
        {
            Text = body, FontSize = 12, Foreground = LabelBrush, TextWrapping = TextWrapping.Wrap
        });
        ToolTip.SetTip(sp, title);
        DetailsPanel.Children.Add(sp);
    }

    private void AddFinding(Finding f)
    {
        var brush = BrushFor(f.Severity);
        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = brush,
            Padding = new Thickness(10, 6, 8, 8),
            Margin = new Thickness(0, 3, 0, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x1F))
        };

        var sp = new StackPanel { Spacing = 3 };
        sp.Children.Add(new TextBlock
        {
            Text = $"{f.Severity.ToString().ToUpperInvariant()}  -  {f.Title}",
            FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = brush, TextWrapping = TextWrapping.Wrap
        });
        sp.Children.Add(new TextBlock
        {
            Text = f.Detail, FontSize = 12, Foreground = LabelBrush, TextWrapping = TextWrapping.Wrap
        });

        border.Child = sp;
        ToolTip.SetTip(border, f.Detail);
        DetailsPanel.Children.Add(border);
    }

    // ------------------------------------------------------------ report

    private string BuildReport() => _result is null ? "" : ReportWriter.Build(_result);

    private async Task CopyReportAsync()
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _result is null) return;
        await clipboard.SetTextAsync(BuildReport());
        StatusText.Text = "Report copied to the clipboard.";
    }

    private async Task SaveReportAsync()
    {
        var top = GetTopLevel(this);
        if (top?.StorageProvider is null || _result is null) return;

        string suggested = Path.GetFileNameWithoutExtension(_result.Meta.FileName ?? "depthview") + "-report.txt";

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save analysis report",
            SuggestedFileName = suggested,
            DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } } }
        });

        if (file is null) return;

        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        await w.WriteAsync(BuildReport());
        StatusText.Text = $"Report saved as {file.Name}.";
    }

    // ------------------------------------------------------------ small helpers

    private static long RawBytes(AnalysisResult r) => Fmt.RawBytes(r);
    private static string FormatBytes(long b) => Fmt.Bytes(b);
    private static string Pct(long part, long whole) => Fmt.Pct(part, whole);
    private static string Truncate(string s, int n) => Fmt.Truncate(s, n);
}
