using System;
using System.Collections.Generic;
using System.Linq;

namespace DepthView.Ablation;

/// <summary>One square's place on a coupon, in coupon millimetres (origin the top-left of the coupon's bounding box, Y down).</summary>
public sealed record PatchCell(int Row, int Column, double X, double Y, string Label);

/// <summary>
/// Where the patches go on a coupon. A grid of squares a gap apart, inside a margin; on a
/// coin only the squares that lie wholly inside the circle (less the margin) are used. The
/// last usable square - bottom-most row, right-most column - is reserved for the coupon's
/// test number (QR code and digits), so every coupon of a study has the same number of
/// settings squares: <see cref="Capacity"/>.
///
/// The gap is what the measure phase needs: a reading on the untouched metal beside each
/// patch is the local zero that cancels the coupon's bow, so the gap can never be less than a
/// gauge tip needs (about 1.5 mm for a 2 mm ball).
/// </summary>
public sealed class PatchLayout
{
    public CouponSpec Coupon { get; }
    public double PatchSizeMm { get; }
    public double GapMm { get; }
    public double MarginMm { get; }
    public int Rows { get; }
    public int Columns { get; }
    /// <summary>Every usable square, row by row; the last is the ID square.</summary>
    public IReadOnlyList<PatchCell> Cells { get; }
    /// <summary>The square that carries the test number.</summary>
    public PatchCell? IdCell => Cells.Count > 0 ? Cells[^1] : null;
    /// <summary>Squares available for settings.</summary>
    public int Capacity => Math.Max(0, Cells.Count - 1);
    /// <summary>The settings squares (every usable square but the ID one).</summary>
    public IEnumerable<PatchCell> SettingsCells => Cells.Take(Capacity);

    public double PitchMm => PatchSizeMm + GapMm;

    private PatchLayout(CouponSpec coupon, double patch, double gap, double margin, int rows, int cols, List<PatchCell> cells)
    {
        Coupon = coupon; PatchSizeMm = patch; GapMm = gap; MarginMm = margin; Rows = rows; Columns = cols; Cells = cells;
    }

    public static PatchLayout For(CouponSpec coupon, double patchMm, double gapMm, double marginMm)
    {
        patchMm = Math.Max(1, patchMm);
        gapMm = Math.Max(0.2, gapMm);
        marginMm = Math.Max(0, marginMm);
        double w = coupon.BoxWidthMm, h = coupon.BoxHeightMm;
        double usableW = w - 2 * marginMm, usableH = h - 2 * marginMm;
        double pitch = patchMm + gapMm;
        int cols = Math.Max(0, (int)Math.Floor((usableW + gapMm) / pitch + 1e-9));
        int rows = Math.Max(0, (int)Math.Floor((usableH + gapMm) / pitch + 1e-9));
        var cells = new List<PatchCell>();
        if (cols > 0 && rows > 0)
        {
            // Centre the grid in the coupon so the margins are even.
            double gridW = cols * pitch - gapMm, gridH = rows * pitch - gapMm;
            double x0 = (w - gridW) / 2, y0 = (h - gridH) / 2;
            double r = coupon.DiameterMm / 2 - marginMm, cx = w / 2, cy = h / 2;
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                {
                    double x = x0 + col * pitch, y = y0 + row * pitch;
                    if (coupon.Shape == CouponShape.Coin && !InsideCircle(x, y, patchMm, cx, cy, r)) continue;
                    cells.Add(new PatchCell(row, col, x, y, Label(row, col)));
                }
        }
        return new PatchLayout(coupon, patchMm, gapMm, marginMm, rows, cols, cells);
    }

    private static bool InsideCircle(double x, double y, double s, double cx, double cy, double r)
    {
        foreach (var (px, py) in new[] { (x, y), (x + s, y), (x, y + s), (x + s, y + s) })
            if ((px - cx) * (px - cx) + (py - cy) * (py - cy) > r * r) return false;
        return true;
    }

    /// <summary>Spreadsheet-style: letters are columns, numbers rows; A1 top-left.</summary>
    public static string Label(int row, int col)
    {
        var s = "";
        col++;
        while (col > 0) { int m = (col - 1) % 26; s = (char)('A' + m) + s; col = (col - 1) / 26; }
        return $"{s}{row + 1}";
    }

    /// <summary>Whether a label strip for the digits fits under the grid inside the margin.</summary>
    public double LabelHeightMm => Math.Clamp(PatchSizeMm * 0.35, 1.2, 3.0);

    public string Summary => Coupon.Shape == CouponShape.Coin
        ? $"{Capacity} settings per coin ({Cells.Count} squares of {PatchSizeMm:0.#} mm inside the {Coupon.DiameterMm:0.#} mm blank, one for the number)"
        : $"{Capacity} settings per sheet ({Columns} x {Rows} squares of {PatchSizeMm:0.#} mm, one for the number)";
}
