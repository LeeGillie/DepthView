using System;
using System.Collections.Generic;

namespace DepthView.Processing;

/// <summary>What has to clear the rim before the ring is painted.</summary>
public enum FitPolicy
{
    /// <summary>Leave the canvas alone. Anything past the rim gets painted over.</summary>
    None,

    /// <summary>
    /// Grow until the furthest engraved pixel clears the rim. Uses as much of the blank as
    /// the design actually needs, because the corners of most coin art are background.
    /// </summary>
    Content,

    /// <summary>
    /// Grow until all four corners of the original clear the rim. Nothing can be clipped by
    /// construction, at the cost of the diagonal: a square that must fit inside a circle
    /// gives up a factor of root two, so a 40 mm blank carries about 27 mm of art.
    /// </summary>
    Canvas,
}

/// <summary>What the new ring of space between the artwork and the rim is cut to.</summary>
public enum PadFill
{
    /// <summary>
    /// Carry the design's own background out to the rim. The field is continuous, with no step
    /// where the original file ended - but on art with a cut-away floor it means engraving that
    /// whole ring to full depth, which is real time on the machine.
    /// </summary>
    Background,

    /// <summary>
    /// Leave the new ring alone: raised rim, original surface, then the design. Costs nothing
    /// to cut. Only sensible when the design's own background is already near untouched, since
    /// otherwise the boundary of the original file shows as a square step.
    /// </summary>
    Untouched,
}

/// <summary>
/// Growing the canvas so a design fits inside the rim, rather than painting over the part
/// that does not.
///
/// The alternative is to scale the artwork down, and padding beats it on the one thing this
/// program cares about: <b>padding does not resample.</b> Every original pixel keeps its exact
/// value and its exact neighbour, and the new pixels are all one constant. Scaling would
/// interpolate, which invents levels that were never in the file - the precise fault DepthView
/// exists to detect. Doing that here to make the artwork fit would be indefensible.
///
/// The physical result is identical either way, because the blank does not change size: after
/// padding, the same artwork spans fewer millimetres of a 40 mm coin. What changes is that the
/// pixels arrive at the laser untouched, and at a finer effective resolution than before, since
/// the same millimetre now holds more of them.
/// </summary>
public static class DepthCanvas
{
    public readonly record struct FitPlan(
        int Size,               // side of the new square canvas, in pixels
        int OffsetX, int OffsetY,
        double ContentRadius,   // what had to clear the rim, in source pixels
        double CornerRadius,    // half-diagonal of the original, for comparison
        ushort Background,      // the level the padding is filled with
        double ArtAcrossMm,     // what the original now measures on the blank
        double PixelsPerMm)     // resolution of the padded canvas
    {
        public bool Grows(int w, int h) => Size > w || Size > h;
    }

    /// <summary>
    /// The level the design sits on, taken as the most common value around the border.
    ///
    /// Deliberately not a brightness threshold. "Anything above a fifth of the range is
    /// content" only holds for art on a black floor; it reads a white-floor map exactly
    /// backwards, and inverts again the moment someone ticks Invert. What is always true is
    /// that the outside edge of a depth map is background, whatever value that happens to be.
    /// </summary>
    public static ushort BackgroundLevel(ushort[] p, int w, int h)
    {
        var counts = new Dictionary<ushort, int>();

        void Bump(ushort v)
        {
            counts.TryGetValue(v, out int c);
            counts[v] = c + 1;
        }

        long last = (long)(h - 1) * w;
        for (int x = 0; x < w; x++) { Bump(p[x]); Bump(p[last + x]); }
        for (int y = 0; y < h; y++) { long r = (long)y * w; Bump(p[r]); Bump(p[r + w - 1]); }

        ushort best = 0;
        int bestCount = -1;
        foreach (var kv in counts)
            if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
        return best;
    }

    /// <summary>Distance from the centre to the furthest pixel that is not background.</summary>
    public static double ContentRadius(ushort[] p, int w, int h, int maxValue,
                                       ushort background, out long contentPixels)
    {
        // A small tolerance, not zero: the floor may carry dither or sensor noise. It can be
        // small because this runs after the level points have been applied, so a floor the
        // user flattened is already exactly uniform by the time we look at it.
        int tol = Math.Max(1, maxValue / 128);
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        double furthestSq = 0;
        long count = 0;

        for (int y = 0; y < h; y++)
        {
            double dy = y - cy;
            long row = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                if (Math.Abs(p[row + x] - background) <= tol) continue;
                count++;
                double dx = x - cx;
                double r2 = dx * dx + dy * dy;
                if (r2 > furthestSq) furthestSq = r2;
            }
        }

        contentPixels = count;
        return Math.Sqrt(furthestSq);
    }

    /// <summary>
    /// Work out the canvas that puts everything inside the rim.
    ///
    /// The target is the <i>inner</i> edge of the ramp, not of the rim, so the design meets
    /// the shoulder rather than being eaten by it.
    /// </summary>
    public static FitPlan Plan(ushort[] p, int w, int h, int maxValue, TuningOptions o,
                               ushort background)
    {
        double blank = o.BlankDiameterMm ?? 0;
        if (blank <= 0) return default;

        double blankRadius = blank / 2.0;
        double clearMm = blankRadius - (o.RimWidthMm ?? 0) - (o.RimRampMm ?? 0);
        if (clearMm <= 0) return default;

        double fraction = clearMm / blankRadius;
        double cornerRadius = Math.Sqrt(Sq((w - 1) / 2.0) + Sq((h - 1) / 2.0));

        double needRadius = o.Fit == FitPolicy.Canvas
            ? cornerRadius
            : ContentRadius(p, w, h, maxValue, background, out _);

        // An image with no content at all - a blank plate - would otherwise ask for a canvas
        // of zero. Fall back to containing the whole thing, which is the safe reading.
        if (needRadius <= 0) needRadius = cornerRadius;

        int size = Math.Max((int)Math.Ceiling(2 * needRadius / fraction), Math.Max(w, h));

        return new FitPlan(
            Size: size,
            OffsetX: (size - w) / 2,
            OffsetY: (size - h) / 2,
            ContentRadius: needRadius,
            CornerRadius: cornerRadius,
            Background: background,
            ArtAcrossMm: Math.Max(w, h) / (double)size * blank,
            PixelsPerMm: size / blank);
    }

    /// <summary>Copy the map into the middle of a larger square canvas filled with one value.</summary>
    public static ushort[] Pad(ushort[] src, int w, int h, int size, int ox, int oy, ushort fill)
    {
        var dst = new ushort[(long)size * size];
        if (fill != 0) Array.Fill(dst, fill);

        for (int y = 0; y < h; y++)
            Array.Copy(src, (long)y * w, dst, (long)(y + oy) * size + ox, w);

        return dst;
    }

    private static double Sq(double v) => v * v;
}
