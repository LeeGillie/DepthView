using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DepthView.Controls;

/// <summary>
/// Grey-level histogram with exact per-level hover readout, log/linear scaling,
/// wheel zoom, and a "comb" strip underneath that lights up every level actually
/// present. The comb is the fastest visual tell for an imposter: an 8-bit map in a
/// 16-bit container draws 256 evenly spaced teeth instead of a solid bar.
/// </summary>
public class HistogramControl : Control
{
    private long[]? _hist;
    private bool[]? _used;
    private int _fullLo, _fullHi;
    private int _lo, _hi;
    private long _total;

    private Column[] _cols = Array.Empty<Column>();
    private double _colsWidth = -1;
    private int _colsLo = -1, _colsHi = -1;
    private long _colsPeak;

    private int _hoverCol = -1;
    private Point _hoverPt;

    private bool _log = true;

    private const double PadL = 62, PadR = 16, PadT = 14, PadB = 46;
    private const double CombH = 11;

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xE8));
    private static readonly IBrush BarDim = new SolidColorBrush(Color.FromRgb(0x3A, 0x5A, 0x82));
    private static readonly IBrush CombBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0xD4, 0x9C));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly IBrush StrongText = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF0));
    private static readonly IBrush TipBack = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36)), 1);
    private static readonly IPen CrossPen = new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x5C)), 1);
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x41)), 1);

    private readonly Typeface _face = new(FontFamily.Default);

    private struct Column
    {
        public int Lo, Hi, PeakLevel;
        public long PeakCount, Sum;
        public int UsedBins;
    }

    public HistogramControl()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>Fired whenever the pointer moves over the plot. Null when the pointer leaves.</summary>
    public event EventHandler<string?>? HoverTextChanged;

    public bool LogScale
    {
        get => _log;
        set { if (_log == value) return; _log = value; Invalidate(); }
    }

    public void SetData(long[] histogram, bool[]? usedMask = null)
    {
        _hist = histogram;
        _used = usedMask;
        _total = 0;
        _fullLo = 0;
        _fullHi = histogram.Length - 1;

        int first = -1, last = -1;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] == 0) continue;
            if (first < 0) first = i;
            last = i;
            _total += histogram[i];
        }

        if (first < 0) { first = 0; last = Math.Max(0, histogram.Length - 1); }

        _lo = _fullLo;
        _hi = _fullHi;
        Invalidate();
    }

    public void Clear()
    {
        _hist = null;
        _used = null;
        _hoverCol = -1;
        Invalidate();
    }

    /// <summary>
    /// Drops any hover readout. Without this a captured screenshot can be left showing a
    /// tooltip for wherever the pointer happened to be sitting when the window opened.
    /// </summary>
    public void ClearHover()
    {
        if (_hoverCol == -1) return;
        _hoverCol = -1;
        HoverTextChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    public void SetRange(int lo, int hi)
    {
        if (_hist is null) return;
        _lo = Math.Clamp(lo, _fullLo, _fullHi);
        _hi = Math.Clamp(hi, _lo + 1, _fullHi);
        Invalidate();
    }

    public void ResetZoom() => SetRange(_fullLo, _fullHi);

    public (int Lo, int Hi) Range => (_lo, _hi);

    private void Invalidate()
    {
        _colsWidth = -1;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- geometry

    private Rect PlotRect => new(PadL, PadT,
        Math.Max(1, Bounds.Width - PadL - PadR),
        Math.Max(1, Bounds.Height - PadT - PadB));

    private void BuildColumns(Rect plot)
    {
        if (_hist is null) { _cols = Array.Empty<Column>(); return; }
        if (Math.Abs(_colsWidth - plot.Width) < 0.5 && _colsLo == _lo && _colsHi == _hi) return;

        int n = Math.Max(1, (int)plot.Width);
        var cols = new Column[n];
        int span = _hi - _lo + 1;
        long peak = 0;

        for (int c = 0; c < n; c++)
        {
            int b0 = _lo + (int)((long)c * span / n);
            int b1 = _lo + (int)((long)(c + 1) * span / n) - 1;
            if (b1 < b0) b1 = b0;
            if (b1 > _hi) b1 = _hi;

            long best = 0, sum = 0;
            int arg = b0, usedBins = 0;
            for (int b = b0; b <= b1; b++)
            {
                long v = _hist[b];
                sum += v;
                if (v > 0) usedBins++;
                if (v > best) { best = v; arg = b; }
            }

            cols[c] = new Column { Lo = b0, Hi = b1, PeakLevel = arg, PeakCount = best, Sum = sum, UsedBins = usedBins };
            if (best > peak) peak = best;
        }

        _cols = cols;
        _colsWidth = plot.Width;
        _colsLo = _lo;
        _colsHi = _hi;
        _colsPeak = peak;
    }

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext ctx)
    {
        var full = new Rect(Bounds.Size);
        ctx.FillRectangle(Backdrop, full);

        var plot = PlotRect;
        ctx.DrawRectangle(null, FramePen, plot);

        if (_hist is null || _total == 0)
        {
            DrawText(ctx, "Load an image to see its grey-level histogram.",
                new Point(plot.X + 14, plot.Y + plot.Height / 2 - 8), TextBrush, 12);
            return;
        }

        BuildColumns(plot);

        // horizontal grid
        for (int i = 1; i < 4; i++)
        {
            double y = plot.Y + plot.Height * i / 4.0;
            ctx.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }

        double peakScale = _colsPeak <= 0 ? 1 : _colsPeak;
        double logPeak = Math.Log10(1 + peakScale);

        for (int c = 0; c < _cols.Length; c++)
        {
            var col = _cols[c];
            if (col.PeakCount <= 0) continue;

            double frac = _log
                ? Math.Log10(1 + col.PeakCount) / logPeak
                : col.PeakCount / peakScale;

            double h = Math.Max(1, frac * plot.Height);
            double x = plot.X + c;
            ctx.FillRectangle(c == _hoverCol ? StrongText : BarBrush,
                new Rect(x, plot.Bottom - h, 1, h));
        }

        // comb strip: which levels are occupied at all
        double combY = plot.Bottom + 6;
        ctx.FillRectangle(BarDim, new Rect(plot.X, combY, plot.Width, CombH));
        for (int c = 0; c < _cols.Length; c++)
        {
            if (_cols[c].UsedBins == 0) continue;
            ctx.FillRectangle(CombBrush, new Rect(plot.X + c, combY, 1, CombH));
        }

        DrawAxisLabels(ctx, plot);
        DrawYAxis(ctx, plot);

        if (_hoverCol >= 0 && _hoverCol < _cols.Length)
            DrawHover(ctx, plot);
    }

    private void DrawYAxis(DrawingContext ctx, Rect plot)
    {
        string top = _log ? $"{_colsPeak:N0} (log)" : $"{_colsPeak:N0}";
        DrawText(ctx, top, new Point(6, plot.Y - 2), TextBrush, 10);
        DrawText(ctx, "0", new Point(6, plot.Bottom - 12), TextBrush, 10);
        DrawText(ctx, "px/level", new Point(6, plot.Y + plot.Height / 2 - 6), TextBrush, 10);
    }

    private void DrawAxisLabels(DrawingContext ctx, Rect plot)
    {
        double y = plot.Bottom + 6 + CombH + 3;
        for (int i = 0; i <= 4; i++)
        {
            int level = _lo + (int)((long)(_hi - _lo) * i / 4);
            double x = plot.X + plot.Width * i / 4.0;
            var brush = TextBrush;
            string s = level.ToString("N0", CultureInfo.InvariantCulture);
            double off = i == 0 ? 0 : i == 4 ? -34 : -14;
            DrawText(ctx, s, new Point(x + off, y), brush, 10);
        }
    }

    private void DrawHover(DrawingContext ctx, Rect plot)
    {
        var col = _cols[_hoverCol];
        double x = plot.X + _hoverCol + 0.5;
        ctx.DrawLine(CrossPen, new Point(x, plot.Y), new Point(x, plot.Bottom + 6 + CombH));

        string line1 = col.Lo == col.Hi
            ? $"Level {col.Lo:N0}"
            : $"Levels {col.Lo:N0} - {col.Hi:N0}  ({col.UsedBins:N0} occupied)";
        string line2 = col.PeakCount == 0
            ? "no pixels here"
            : $"{col.PeakCount:N0} px at level {col.PeakLevel:N0}   ({100.0 * col.PeakCount / _total:F4}%)";
        string line3 = col.Lo == col.Hi
            ? $"{100.0 * col.Sum / _total:F4}% of pixels"
            : $"column total {col.Sum:N0} px  ({100.0 * col.Sum / _total:F4}%)";

        var t1 = Text(line1, StrongText, 12);
        var t2 = Text(line2, TextBrush, 11);
        var t3 = Text(line3, TextBrush, 11);

        double w = Math.Max(t1.Width, Math.Max(t2.Width, t3.Width)) + 18;
        double h = t1.Height + t2.Height + t3.Height + 14;
        double bx = _hoverPt.X + 16;

        // The readout is about 55px tall, and on a short window the plot can be shorter than
        // that. Clamping blindly then asks Math.Clamp for a range whose minimum is above its
        // maximum, which throws inside the render pass and takes the whole program down. When
        // the box will not fit, pin it to the top of the plot and let it overflow instead:
        // a readout hanging over the axis is worth more than a crash.
        double topLimit = plot.Y + 2;
        double bottomLimit = plot.Bottom - h - 2;
        double by = bottomLimit > topLimit
            ? Math.Clamp(_hoverPt.Y - h - 10, topLimit, bottomLimit)
            : topLimit;
        if (bx + w > plot.Right) bx = _hoverPt.X - w - 16;
        if (bx < plot.X) bx = plot.X + 2;

        var box = new Rect(bx, by, w, h);
        ctx.DrawRectangle(TipBack, FramePen, box, 4, 4);
        ctx.DrawText(t1, new Point(bx + 9, by + 5));
        ctx.DrawText(t2, new Point(bx + 9, by + 5 + t1.Height));
        ctx.DrawText(t3, new Point(bx + 9, by + 5 + t1.Height + t2.Height));
    }

    private FormattedText Text(string s, IBrush brush, double size)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, size, brush);

    private void DrawText(DrawingContext ctx, string s, Point at, IBrush brush, double size)
        => ctx.DrawText(Text(s, brush, size), at);

    // ---------------------------------------------------------------- input

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_hist is null) return;

        var plot = PlotRect;
        var p = e.GetPosition(this);
        _hoverPt = p;

        int c = (int)(p.X - plot.X);
        if (p.X < plot.X || p.X > plot.Right || c < 0 || c >= _cols.Length)
        {
            if (_hoverCol != -1) { _hoverCol = -1; HoverTextChanged?.Invoke(this, null); InvalidateVisual(); }
            return;
        }

        _hoverCol = c;
        var col = _cols[c];
        string range = col.Lo == col.Hi ? $"level {col.Lo:N0}" : $"levels {col.Lo:N0}-{col.Hi:N0}";
        HoverTextChanged?.Invoke(this,
            $"{range}   peak {col.PeakCount:N0} px at {col.PeakLevel:N0}   " +
            $"column {col.Sum:N0} px ({(_total > 0 ? 100.0 * col.Sum / _total : 0):F4}%)   " +
            $"{col.UsedBins:N0} occupied level(s)");
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverCol = -1;
        HoverTextChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_hist is null) return;

        var plot = PlotRect;
        var p = e.GetPosition(this);
        if (p.X < plot.X || p.X > plot.Right) return;

        int span = _hi - _lo + 1;
        double frac = Math.Clamp((p.X - plot.X) / plot.Width, 0, 1);
        int anchor = _lo + (int)(frac * span);

        double factor = e.Delta.Y > 0 ? 0.7 : 1 / 0.7;
        int fullSpan = _fullHi - _fullLo + 1;
        int newSpan = (int)Math.Clamp(Math.Round(span * factor), 2, fullSpan);

        int lo = anchor - (int)(frac * newSpan);
        lo = Math.Clamp(lo, _fullLo, _fullHi - newSpan + 1);
        SetRange(lo, lo + newSpan - 1);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            ResetZoom();
            e.Handled = true;
        }
    }
}
