using System;
using System.Collections.Generic;
using QRCoder;

namespace DepthView.Ablation;

/// <summary>
/// The test number as a mark on the coupon: a QR code holding "DV&lt;number&gt;" (QR version 1,
/// 21 x 21 modules, with error correction high enough to survive a scratched corner) and the
/// digits beside the grid for people. The measure phase reads either.
/// </summary>
public static class TestNumberMark
{
    public const string Prefix = "DV";

    public static string Text(int number) => $"{Prefix}{number}";

    /// <summary>The number back from a scanned code, or null when it is not one of ours.</summary>
    public static int? Parse(string? scanned) =>
        scanned is not null && scanned.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && int.TryParse(scanned.AsSpan(Prefix.Length), out int n) && n > 0 ? n : null;

    /// <summary>The dark modules of the code, without the quiet zone, [row, column].</summary>
    public static bool[,] Modules(int number)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(Text(number), QRCodeGenerator.ECCLevel.Q);
        var m = data.ModuleMatrix;           // includes a 4-module quiet zone on every side
        const int q = 4;
        int n = m.Count - 2 * q;
        var dark = new bool[n, n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                dark[r, c] = m[r + q][c + q];
        return dark;
    }

    /// <summary>
    /// The dark modules as squares in coupon mm, filling a square of <paramref name="sizeMm"/> at
    /// (<paramref name="x0"/>, <paramref name="y0"/>) with one module of quiet zone kept inside
    /// it on every side (the untouched gap around the square is the rest of the quiet zone).
    /// </summary>
    public static List<(double X, double Y, double Size)> Squares(int number, double x0, double y0, double sizeMm)
    {
        var dark = Modules(number);
        int n = dark.GetLength(0);
        double module = sizeMm / (n + 2);
        var list = new List<(double, double, double)>();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                if (dark[r, c]) list.Add((x0 + (c + 1) * module, y0 + (r + 1) * module, module));
        return list;
    }

    /// <summary>Module size for a square of this size, mm - under about 0.3 mm a phone struggles; a USB microscope reads 0.15 mm.</summary>
    public static double ModuleMm(int number, double sizeMm) => sizeMm / (Modules(number).GetLength(0) + 2);
}
