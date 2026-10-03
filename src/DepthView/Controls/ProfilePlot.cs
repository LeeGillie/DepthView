using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DepthView.Controls;

/// <summary>
/// A cross-section of the coin near its edge: depth against distance from the centre, metal
/// below the line, air above. The tuning wizard draws the result's average profile here, so
/// the step into the rim - or the taper that replaces it - can be seen as a shape rather than
/// read as a number. Depth is drawn exaggerated to fill the plot; it says so.
/// </summary>
public sealed class ProfilePlot : Control
{
    private double[] _depth = Array.Empty<double>();
    private double _startMm, _endMm, _rimMm = double.NaN, _rampMm = double.NaN;

    private static readonly IBrush Metal = new SolidColorBrush(Color.Parse("#5E4E33"));
    private static readonly IPen Surface = new Pen(new SolidColorBrush(Color.Parse("#E8C987")), 1.6);
    private static readonly IPen Axis = new Pen(new SolidColorBrush(Color.Parse("#333944")), 1);
    private static readonly IPen RimPen = new Pen(new SolidColorBrush(Color.Parse("#F0B45C")), 1, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen RampPen = new Pen(new SolidColorBrush(Color.Parse("#7FA6D8")), 1, new DashStyle(new double[] { 2, 3 }, 0));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A929E"));
    private static readonly IBrush Plot = new SolidColorBrush(Color.Parse("#0A0C0F"));

    /// <summary>
    /// <paramref name="depth"/>: 0 untouched to 1 full depth, one value per step from
    /// <paramref name="startMm"/> to <paramref name="endMm"/> from the centre. The rim and the
    /// start of the taper are marked where given (NaN for none).
    /// </summary>
    public void SetData(double[] depth, double startMm, double endMm, double rimMm, double rampMm)
    {
        _depth = depth;
        _startMm = startMm;
        _endMm = endMm;
        _rimMm = rimMm;
        _rampMm = rampMm;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width < 40 || b.Height < 30) return;
        var plot = new Rect(8, 16, b.Width - 16, b.Height - 34);
        ctx.FillRectangle(Plot, plot, 4);
        if (_depth.Length < 2 || _endMm <= _startMm) return;

        // Leave a little air above the surface and a little metal below full depth.
        double X(double mm) => plot.X + (mm - _startMm) / (_endMm - _startMm) * plot.Width;
        double Y(double d) => plot.Y + plot.Height * (0.15 + 0.7 * Math.Clamp(d, 0, 1));

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            // Square ends: the surface is carried flat to both sides of the plot before the
            // outline drops to the bottom, or the closing edge reads as a slope that is not there.
            g.BeginFigure(new Point(plot.X, plot.Bottom), true);
            g.LineTo(new Point(plot.X, Y(_depth[0])));
            for (int i = 0; i < _depth.Length; i++)
            {
                double mm = _startMm + (i + 0.5) * (_endMm - _startMm) / _depth.Length;
                g.LineTo(new Point(X(mm), Y(_depth[i])));
            }
            g.LineTo(new Point(plot.Right, Y(_depth[^1])));
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Metal, null, geo);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (int i = 0; i < _depth.Length; i++)
            {
                double mm = _startMm + (i + 0.5) * (_endMm - _startMm) / _depth.Length;
                var pt = new Point(X(mm), Y(_depth[i]));
                if (i == 0) g.BeginFigure(pt, false); else g.LineTo(pt);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, Surface, line);

        if (!double.IsNaN(_rampMm) && _rampMm > _startMm)
            ctx.DrawLine(RampPen, new Point(X(_rampMm), plot.Y), new Point(X(_rampMm), plot.Bottom));
        if (!double.IsNaN(_rimMm))
            ctx.DrawLine(RimPen, new Point(X(_rimMm), plot.Y), new Point(X(_rimMm), plot.Bottom));
        ctx.DrawLine(Axis, new Point(plot.X, Y(0)), new Point(plot.Right, Y(0)));

        Text(ctx, "Average cross-section near the edge - depth exaggerated", new Point(plot.X, 0));
        Text(ctx, $"{_endMm - _startMm:0.0} mm in from the edge", new Point(plot.X, plot.Bottom + 3));
        var edge = Make("edge of the blank");
        ctx.DrawText(edge, new Point(plot.Right - edge.Width, plot.Bottom + 3));
        if (!double.IsNaN(_rimMm))
        {
            var rim = Make("rim");
            ctx.DrawText(rim, new Point(Math.Min(plot.Right - rim.Width, X(_rimMm) + 4), plot.Y + 2));
        }
        if (!double.IsNaN(_rampMm) && _rampMm > _startMm)
        {
            var ramp = Make("taper");
            ctx.DrawText(ramp, new Point(Math.Max(plot.X, X(_rampMm) - ramp.Width - 4), plot.Y + 2));
        }
    }

    private static FormattedText Make(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                        Typeface.Default, 10.5, Label);

    private static void Text(DrawingContext ctx, string s, Point at) => ctx.DrawText(Make(s), at);
}
