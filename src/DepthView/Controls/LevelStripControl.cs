using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DepthView.Controls;

/// <summary>
/// The source histogram with the two level points drawn on it and draggable.
///
/// Separate from <see cref="HistogramControl"/> on purpose. That one is an inspection
/// instrument - zoom, hover readout, the occupancy comb - and bolting editable markers onto
/// it would put two different jobs in one control, where a drag would have to guess whether
/// it meant "move a marker" or "pan the view". This one does nothing but let you place the
/// black and white points and see immediately what they swallow.
///
/// The shaded ends are the whole argument for doing this visually rather than by typing
/// numbers: they show the pixels about to be flattened, against the peak they came from.
/// A floor you meant to clean up looks like a spike sitting inside the shading. A face you
/// did not mean to lose looks like a broad hump half-covered by it.
/// </summary>
public class LevelStripControl : Control
{
    private long[]? _hist;
    private int _maxValue = 255;
    private long _peak;
    private bool _log = true;

    private int _black, _white;
    private int _dragging;          // 0 none, 1 black, 2 white
    private bool _nearMarker;

    private const double PadL = 10, PadR = 10, PadT = 12, PadB = 20;
    private const double GrabPx = 14;

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
    private static readonly IBrush KeptBar = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xE8));
    private static readonly IBrush LostBar = new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x5C));
    private static readonly IBrush BlackShade = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x7A, 0x6E));
    private static readonly IBrush WhiteShade = new SolidColorBrush(Color.FromArgb(0x38, 0xE8, 0xB0, 0x4B));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly IBrush Faint = new SolidColorBrush(Color.FromRgb(0x5E, 0x67, 0x73));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x41)), 1);
    private static readonly IPen BlackPen = new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0x7A, 0x6E)), 1.6);
    private static readonly IPen WhitePen = new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B)), 1.6);

    private readonly Typeface _face = new(FontFamily.Default);

    public LevelStripControl()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>Raised while either marker is being dragged, and once when it is released.</summary>
    public event EventHandler? LevelsChanged;

    public int Black => _black;
    public int White => _white;
    public int MaxValue => _maxValue;

    public bool LogScale
    {
        get => _log;
        set { if (_log == value) return; _log = value; InvalidateVisual(); }
    }

    public void SetData(long[] histogram, int maxValue)
    {
        _hist = histogram;
        _maxValue = Math.Max(1, maxValue);
        _peak = 0;
        foreach (long c in histogram) if (c > _peak) _peak = c;
        InvalidateVisual();
    }

    /// <summary>Place both markers without raising the change event, for programmatic setup.</summary>
    public void SetLevels(int black, int white)
    {
        _black = Math.Clamp(black, 0, _maxValue);
        _white = Math.Clamp(white, _black + 1, _maxValue);
        InvalidateVisual();
    }

    public void Clear()
    {
        _hist = null;
        InvalidateVisual();
    }

    private Rect PlotRect => new(PadL, PadT,
        Math.Max(1, Bounds.Width - PadL - PadR),
        Math.Max(1, Bounds.Height - PadT - PadB));

    private double XOf(int level, Rect plot) => plot.X + (double)level / _maxValue * plot.Width;

    private int LevelAt(double x, Rect plot)
        => (int)Math.Round(Math.Clamp((x - plot.X) / plot.Width, 0, 1) * _maxValue);

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Backdrop, new Rect(Bounds.Size));
        var plot = PlotRect;
        ctx.DrawRectangle(null, FramePen, plot);

        if (_hist is null || _peak == 0)
        {
            ctx.DrawText(Text("Load an image to place the level points.", TextBrush, 12),
                new Point(plot.X + 12, plot.Y + plot.Height / 2 - 8));
            return;
        }

        // One screen column can span many levels on a 16-bit map, so take the tallest bar in
        // each column rather than the first: a single occupied level in a sea of empty ones is
        // exactly the thing that must not vanish because of where the column boundaries fell.
        int cols = (int)plot.Width;
        double logPeak = Math.Log10(1 + _peak);

        for (int c = 0; c < cols; c++)
        {
            int lo = (int)((long)c * _maxValue / cols);
            int hi = (int)((long)(c + 1) * _maxValue / cols);
            if (hi <= lo) hi = lo + 1;

            long tallest = 0;
            for (int v = lo; v < hi && v < _hist.Length; v++)
                if (_hist[v] > tallest) tallest = _hist[v];
            if (tallest == 0) continue;

            double frac = _log ? Math.Log10(1 + tallest) / logPeak : (double)tallest / _peak;
            double h = Math.Max(1, frac * plot.Height);
            bool kept = lo > _black && hi <= _white;

            ctx.FillRectangle(kept ? KeptBar : LostBar,
                new Rect(plot.X + c, plot.Bottom - h, 1, h));
        }

        double bx = XOf(_black, plot);
        double wx = XOf(_white, plot);

        if (bx > plot.X)
            ctx.FillRectangle(BlackShade, new Rect(plot.X, plot.Y, bx - plot.X, plot.Height));
        if (wx < plot.Right)
            ctx.FillRectangle(WhiteShade, new Rect(wx, plot.Y, plot.Right - wx, plot.Height));

        ctx.DrawLine(BlackPen, new Point(bx, plot.Y), new Point(bx, plot.Bottom));
        ctx.DrawLine(WhitePen, new Point(wx, plot.Y), new Point(wx, plot.Bottom));

        Handle(ctx, bx, plot.Y, BlackPen);
        Handle(ctx, wx, plot.Y, WhitePen);

        Label(ctx, $"black {_black:N0}", bx, plot, false);
        Label(ctx, $"white {_white:N0}", wx, plot, true);

        ctx.DrawText(Text("0", Faint, 10), new Point(plot.X, plot.Bottom + 3));
        var end = Text($"{_maxValue:N0}", Faint, 10);
        ctx.DrawText(end, new Point(plot.Right - end.Width, plot.Bottom + 3));
    }

    private static void Handle(DrawingContext ctx, double x, double top, IPen pen)
        => ctx.DrawRectangle(pen.Brush, null, new Rect(x - 4, top - 8, 8, 8));

    private void Label(DrawingContext ctx, string s, double x, Rect plot, bool rightOfMarker)
    {
        var t = Text(s, TextBrush, 10);
        double at = rightOfMarker ? x + 5 : x - t.Width - 5;
        at = Math.Clamp(at, plot.X + 2, plot.Right - t.Width - 2);
        ctx.DrawText(t, new Point(at, plot.Bottom + 3));
    }

    private FormattedText Text(string s, IBrush brush, double size)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, size, brush);

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_hist is null) return;

        var plot = PlotRect;
        double x = e.GetPosition(this).X;
        double db = Math.Abs(x - XOf(_black, plot));
        double dw = Math.Abs(x - XOf(_white, plot));

        // Within grabbing distance takes that marker. Further away, the nearer marker jumps to
        // the click - the same forgiving behaviour every levels control has, and it saves
        // hunting for a two-pixel line on a 16-bit histogram.
        _dragging = db <= dw ? 1 : 2;
        if (Math.Min(db, dw) > GrabPx) Move(LevelAt(x, plot));

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_hist is null) return;

        var plot = PlotRect;
        double x = e.GetPosition(this).X;

        if (_dragging != 0) { Move(LevelAt(x, plot)); return; }

        bool near = Math.Min(Math.Abs(x - XOf(_black, plot)), Math.Abs(x - XOf(_white, plot))) <= GrabPx;
        if (near == _nearMarker) return;
        _nearMarker = near;
        Cursor = new Cursor(near ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging == 0) return;
        _dragging = 0;
        e.Pointer.Capture(null);
        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The markers cannot cross. Letting them would invert the meaning of the whole dialog
    /// halfway through a drag, which is not a thing anyone means to do with the mouse.
    /// </summary>
    private void Move(int level)
    {
        if (_dragging == 1) _black = Math.Clamp(level, 0, _white - 1);
        else _white = Math.Clamp(level, _black + 1, _maxValue);

        InvalidateVisual();
        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }
}
