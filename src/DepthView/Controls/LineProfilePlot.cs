using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DepthView.Processing;

namespace DepthView.Controls;

/// <summary>
/// Depth along a line drawn across a map (TODO 9.5): the surface the file describes, and the
/// staircase the job will cut at the pass count, one over the other. A single number cannot
/// say how depth progresses across a surface - whether a smooth cheek arrives as a slope or as
/// a flight of steps - and that progression is what decides whether it cuts smooth.
///
/// Depth is stretched to fill the plot, and the plot says so; a spot-wide bar sits under the
/// axis so the treads can be judged against the beam by eye.
/// </summary>
public sealed class LineProfilePlot : Control
{
    private ushort[] _levels = Array.Empty<ushort>();
    private int _max = 65535, _passes = 256;
    private double _lengthMm, _depthMm, _spotUm;
    private string _title = "";

    // Categorical slots 1 and 2 of the reference palette, dark steps, validated against this
    // plot's surface (#0A0C0F): lightness band, CVD and contrast all pass.
    private static readonly IPen MapPen = new Pen(new SolidColorBrush(Color.Parse("#3987e5")), 1.6);
    private static readonly IPen CutPen = new Pen(new SolidColorBrush(Color.Parse("#d95926")), 1.6);
    private static readonly IBrush MapBrush = new SolidColorBrush(Color.Parse("#3987e5"));
    private static readonly IBrush CutBrush = new SolidColorBrush(Color.Parse("#d95926"));
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#20252C")), 1);
    private static readonly IPen ScalePen = new Pen(new SolidColorBrush(Color.Parse("#C7CED8")), 2);
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8A929E"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#C7CED8"));
    private static readonly IBrush Plot = new SolidColorBrush(Color.Parse("#0A0C0F"));

    /// <summary>Layer edges crossed along the line, and the treads between them, for the caption.</summary>
    public int EdgesCrossed { get; private set; }
    public double MedianTreadMicrons { get; private set; }

    /// <summary>
    /// <paramref name="levels"/>: the file's own levels along the line, one per pixel of length,
    /// black deepest. <paramref name="lengthMm"/> is the line's length on the blank.
    /// </summary>
    public void SetData(ushort[] levels, int maxValue, int passes, double lengthMm, double depthMm,
                        double spotUm, string title)
    {
        _levels = levels;
        _max = Math.Max(1, maxValue);
        _passes = Math.Max(2, passes);
        _lengthMm = lengthMm;
        _depthMm = depthMm;
        _spotUm = spotUm;
        _title = title;

        // Treads along the line: runs of one slice. Measured along the line, not across the
        // contours, so a line crossing them at a slant reads them wider than they are.
        var runs = new List<int>();
        int run = 1, edges = 0;
        for (int i = 1; i < levels.Length; i++)
        {
            if (TerraceMap.SliceOf(levels[i], _max, _passes) != TerraceMap.SliceOf(levels[i - 1], _max, _passes))
            {
                edges++;
                runs.Add(run);
                run = 1;
            }
            else run++;
        }
        EdgesCrossed = edges;
        // Interior treads only: the first and last runs are cut off by the ends of the line.
        if (runs.Count > 1) runs.RemoveAt(0);
        runs.Sort();
        double umPerSample = levels.Length > 1 ? lengthMm * 1000 / (levels.Length - 1) : 0;
        MedianTreadMicrons = runs.Count == 0 ? 0 : runs[runs.Count / 2] * umPerSample;
        InvalidateVisual();
    }

    public void Clear()
    {
        _levels = Array.Empty<ushort>();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width < 80 || b.Height < 60) return;
        var plot = new Rect(46, 18, b.Width - 56, b.Height - 46);
        ctx.FillRectangle(Plot, plot, 4);
        int n = _levels.Length;
        if (n < 2 || _lengthMm <= 0) return;

        // Depth in microns below the untouched surface, for the file and for the cut.
        double Map(ushort v) => (1.0 - (double)v / _max) * _depthMm * 1000;
        double Cut(ushort v) => (1.0 - (double)TerraceMap.SliceOf(v, _max, _passes) / (_passes - 1)) * _depthMm * 1000;

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in _levels)
        {
            lo = Math.Min(lo, Math.Min(Map(v), Cut(v)));
            hi = Math.Max(hi, Math.Max(Map(v), Cut(v)));
        }
        if (hi - lo < 1) { double mid = (hi + lo) / 2; lo = mid - 0.5; hi = mid + 0.5; }
        // Air above the untouched surface is not depth; the axis stops at zero.
        double pad = (hi - lo) * 0.08;
        lo = lo >= 0 && lo - pad < 0 ? 0 : lo - pad;
        hi += pad;

        double X(int i) => plot.X + (double)i / (n - 1) * plot.Width;
        double Y(double um) => plot.Y + (um - lo) / (hi - lo) * plot.Height;   // deeper is lower

        // Recessive grid: four depth lines with their values.
        for (int k = 0; k <= 4; k++)
        {
            double um = lo + (hi - lo) * k / 4;
            double y = Y(um);
            ctx.DrawLine(Grid, new Point(plot.X, y), new Point(plot.Right, y));
            var t = Make($"{um:0}", Label);
            ctx.DrawText(t, new Point(plot.X - t.Width - 5, y - t.Height / 2));
        }

        // Thin out to about one point per screen pixel for the smooth line; the staircase keeps
        // every step, because a step is the thing being looked for.
        int stride = Math.Max(1, n / Math.Max(1, (int)plot.Width));
        var map = new StreamGeometry();
        using (var g = map.Open())
        {
            g.BeginFigure(new Point(X(0), Y(Map(_levels[0]))), false);
            for (int i = stride; i < n; i += stride) g.LineTo(new Point(X(i), Y(Map(_levels[i]))));
            g.LineTo(new Point(X(n - 1), Y(Map(_levels[n - 1]))));
            g.EndFigure(false);
        }

        var cut = new StreamGeometry();
        using (var g = cut.Open())
        {
            double prev = Cut(_levels[0]);
            g.BeginFigure(new Point(X(0), Y(prev)), false);
            for (int i = 1; i < n; i++)
            {
                double c = Cut(_levels[i]);
                if (c != prev)
                {
                    g.LineTo(new Point(X(i), Y(prev)));
                    g.LineTo(new Point(X(i), Y(c)));
                    prev = c;
                }
            }
            g.LineTo(new Point(X(n - 1), Y(prev)));
            g.EndFigure(false);
        }
        // The staircase first and the file's surface over it, so where the two agree the thinner
        // blue curve still shows on the orange steps rather than vanishing under them.
        ctx.DrawGeometry(null, CutPen, cut);
        ctx.DrawGeometry(null, MapPen, map);

        // Title and legend above the plot; two series, so a legend, and each is also a
        // different shape - a curve and a staircase - so colour is never the only cue.
        ctx.DrawText(Make(_title, Ink), new Point(plot.X, 0));
        var l2 = Make($"as cut at {_passes:N0} passes", Label);
        var l1 = Make("the file", Label);
        double lx = plot.Right - l2.Width;
        ctx.DrawText(l2, new Point(lx, 1));
        ctx.FillRectangle(CutBrush, new Rect(lx - 16, 7, 11, 2.5));
        double lx1 = lx - 16 - 14 - l1.Width;
        ctx.DrawText(l1, new Point(lx1, 1));
        ctx.FillRectangle(MapBrush, new Rect(lx1 - 16, 7, 11, 2.5));

        // Along the bottom: length, the spot as a bar to judge treads against, and a reminder
        // that depth is stretched.
        double y0 = plot.Bottom + 6;
        ctx.DrawText(Make($"{_lengthMm:0.00} mm along the line", Label), new Point(plot.X, y0 + 6));
        double spotPx = _spotUm / 1000 / _lengthMm * plot.Width;
        if (spotPx >= 1 && spotPx < plot.Width / 3)
        {
            double sx = plot.X + plot.Width * 0.42;
            ctx.DrawLine(ScalePen, new Point(sx, y0 + 12), new Point(sx + spotPx, y0 + 12));
            ctx.DrawText(Make($"spot {_spotUm:0} um", Label), new Point(sx + spotPx + 6, y0 + 6));
        }
        var note = Make($"depth in um, stretched to fit ({hi - lo:0} um top to bottom)", Label);
        ctx.DrawText(note, new Point(plot.Right - note.Width, y0 + 6));
    }

    private static FormattedText Make(string s, IBrush brush) => new(s, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, Typeface.Default, 10.5, brush);
}
