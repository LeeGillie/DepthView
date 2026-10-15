using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DepthView.Ablation;

namespace DepthView.Controls;

/// <summary>
/// A coupon drawn to scale: its outline (rectangle or circle), every settings square with its
/// label, the test-number square, the crosses, and - when a coupon is shown - a tint per
/// square by its planned dose, deepest darkest. Hovering a square names its settings.
/// </summary>
public sealed class CouponPreview : Control
{
    private Study? _study;
    private Coupon? _coupon;
    private int _hover = -1;

    public Action<string>? HoverText { get; set; }

    public void Show(Study? study, Coupon? coupon)
    {
        _study = study;
        _coupon = coupon;
        _hover = -1;
        InvalidateVisual();
    }

    public CouponPreview()
    {
        ClipToBounds = true;
        PointerMoved += (_, e) =>
        {
            int h = HitTest(e.GetPosition(this));
            if (h == _hover) return;
            _hover = h;
            InvalidateVisual();
            HoverText?.Invoke(Describe(h));
        };
        PointerExited += (_, _) => { _hover = -1; InvalidateVisual(); HoverText?.Invoke(""); };
    }

    private (double s, double ox, double oy) Fit()
    {
        if (_study is null) return (1, 0, 0);
        double w = _study.Coupon.BoxWidthMm, h = _study.Coupon.BoxHeightMm;
        double s = Math.Min((Bounds.Width - 20) / w, (Bounds.Height - 20) / h);
        if (!(s > 0) || double.IsInfinity(s)) s = 1;
        return (s, (Bounds.Width - w * s) / 2, (Bounds.Height - h * s) / 2);
    }

    private int HitTest(Point p)
    {
        if (_study is null) return -1;
        var l = _study.Layout;
        var (s, ox, oy) = Fit();
        for (int i = 0; i < l.Cells.Count; i++)
        {
            var c = l.Cells[i];
            double x = ox + c.X * s, y = oy + c.Y * s, sz = l.PatchSizeMm * s;
            if (p.X >= x && p.X <= x + sz && p.Y >= y && p.Y <= y + sz) return i;
        }
        return -1;
    }

    private string Describe(int i)
    {
        if (_study is null || i < 0) return "";
        var l = _study.Layout;
        if (i == l.Capacity) return _coupon is null ? "The test number (QR code and digits)." : _coupon.IsReference ? "A reference coupon has no test number: nothing on it was cut." : $"Test number DV{_coupon.Number} - QR code and digits.";
        var patch = _coupon?.Patches.FirstOrDefault(p => p.Index == i);
        if (patch is null) return $"{l.Cells[i].Label}: empty on this coupon.";
        var s = patch.Settings;
        double rated = _study.Laser.RatedPowerW;
        string depth = patch.Measurement?.MeanDepthUm is double d
            ? patch.Status == PatchStatus.Predicted ? $" Reported {d:0} µm ({patch.Measurement.Method})." : $" Measured {d:0} µm."
            : patch.Status == PatchStatus.Predicted ? " No depth reported." : "";
        return $"{patch.Label}: {s.ToShortString(_study.Laser.Kind)} - {s.PowerW(rated):0.#} W, {s.DosePerLayerJPerMm2(rated):0.00} J/mm² per layer, " +
               $"{s.TotalDoseJPerMm2(rated):0.0} J/mm² in all. {patch.Reason}{depth}" +
               (patch.Provenance is { } prov ? $" Source: {prov.Summary}." : "");
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0E1013")), new Rect(Bounds.Size));
        if (_study is null) return;
        var l = _study.Layout;
        var (s, ox, oy) = Fit();
        var metal = new SolidColorBrush(Color.Parse("#B8933E"));
        var edge = new Pen(new SolidColorBrush(Color.Parse("#E2C56E")), 1.5);
        double w = _study.Coupon.BoxWidthMm * s, h = _study.Coupon.BoxHeightMm * s;
        if (_study.Coupon.Shape == CouponShape.Coin)
            ctx.DrawEllipse(metal, edge, new Point(ox + w / 2, oy + h / 2), w / 2, h / 2);
        else
        {
            double r = _study.Coupon.CornerRadiusMm * s;
            ctx.DrawRectangle(metal, edge, new RoundedRect(new Rect(ox, oy, w, h), r));
        }

        double rated = _study.Laser.RatedPowerW;
        // Tint by dose on a log scale: the design spans decades, and a linear tint made every
        // patch but the hottest look the same.
        var doses = _coupon?.Patches.Select(p => p.Settings.TotalDoseJPerMm2(rated)).Where(d => d > 0).ToList() ?? [];
        double logMin = doses.Count > 0 ? Math.Log(doses.Min()) : 0, logMax = doses.Count > 0 ? Math.Log(doses.Max()) : 1;
        var labelBrush = new SolidColorBrush(Color.Parse("#2A2110"));
        var tf = new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold);
        for (int i = 0; i < l.Cells.Count; i++)
        {
            var c = l.Cells[i];
            double x = ox + c.X * s, y = oy + c.Y * s, sz = l.PatchSizeMm * s;
            bool isId = i == l.Capacity;
            var patch = _coupon?.Patches.FirstOrDefault(p => p.Index == i);
            IBrush fill;
            if (isId) fill = new SolidColorBrush(Color.Parse("#1A1A1A"));
            else if (patch is null) fill = new SolidColorBrush(Color.Parse("#A07F35"));
            else
            {
                double dose = patch.Settings.TotalDoseJPerMm2(rated);
                double t = dose > 0 && logMax > logMin ? Math.Clamp((Math.Log(dose) - logMin) / (logMax - logMin), 0, 1) : 0.5;
                byte g = (byte)(150 - 110 * t);
                fill = new SolidColorBrush(Color.FromRgb((byte)(g + 20), (byte)(g - 10 > 0 ? g - 10 : 0), (byte)(g / 3)));
            }
            var pen = i == _hover ? new Pen(Brushes.White, 2) : new Pen(new SolidColorBrush(Color.Parse("#5A4A20")), 0.8);
            ctx.DrawRectangle(fill, pen, new Rect(x, y, sz, sz));
            if (isId && _coupon is { IsReference: false } && sz > 14)
            {
                // The QR as it will be, scaled: a dot per dark module.
                var dark = TestNumberMark.Modules(_coupon.Number);
                int n = dark.GetLength(0);
                double m = sz / (n + 2);
                var ink = new SolidColorBrush(Color.Parse("#E6E6E6"));
                for (int r = 0; r < n; r++)
                    for (int q = 0; q < n; q++)
                        if (dark[r, q]) ctx.FillRectangle(ink, new Rect(x + (q + 1) * m, y + (r + 1) * m, m, m));
            }
            else if (sz > 18)
            {
                var ft = new FormattedText(isId ? "ID" : c.Label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, Math.Clamp(sz * 0.3, 8, 13), isId ? Brushes.White : labelBrush);
                ctx.DrawText(ft, new Point(x + 2, y + 1));
            }
        }

        // Crosses and digits, where the job puts them.
        var mark = new Pen(new SolidColorBrush(Color.Parse("#1A1A1A")), 1.2);
        foreach (var (cx, cy) in CouponJob.CrossCentres(_study, 0, 0))
        {
            double half = CouponJob.CrossMm / 2 * s;
            ctx.DrawLine(mark, new Point(ox + cx * s - half, oy + cy * s), new Point(ox + cx * s + half, oy + cy * s));
            ctx.DrawLine(mark, new Point(ox + cx * s, oy + cy * s - half), new Point(ox + cx * s, oy + cy * s + half));
        }
        if (_coupon is { IsReference: true })
        {
            // A reference coupon was never cut: say so where the number would be.
            var ft = new FormattedText($"reference: {_coupon.Name} - not cut here", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, new SolidColorBrush(Color.Parse("#1A1A1A")));
            double gridBottom = l.Cells.Count > 0 ? l.Cells.Max(c => c.Y) + l.PatchSizeMm : l.Coupon.BoxHeightMm - l.MarginMm;
            ctx.DrawText(ft, new Point(ox + (l.MarginMm + CouponJob.CrossMm + 1) * s, oy + gridBottom * s + 1));
            return;
        }
        foreach (var line in CouponJob.DigitStrokes(_study, _coupon?.Number ?? 0, 0, 0))
            for (int i = 0; i + 1 < line.Count; i++)
                ctx.DrawLine(mark, new Point(ox + line[i].X * s, oy + line[i].Y * s), new Point(ox + line[i + 1].X * s, oy + line[i + 1].Y * s));
    }
}
