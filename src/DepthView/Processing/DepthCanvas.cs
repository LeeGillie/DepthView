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

    /// <summary>
    /// Centre the blank on the design and size it to the design: the furthest engraved pixel
    /// from the design's own centre lands on the inside of the rim. For art that is not where
    /// the canvas centre is - a coin drawn off-centre on a tall canvas with a wide black
    /// surround - the other two policies centre the blank on the canvas and leave a lopsided
    /// moat of background between the design and the rim. This one crops that surround away
    /// instead (or pads, if the design runs to the edge). Cropping removes only background;
    /// nothing is resampled either way.
    /// </summary>
    Design,
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
        double ArtAcrossMm,     // what the original now measures on the blank; with Design, the design
        double PixelsPerMm,     // resolution of the padded canvas
        bool Recentred = false, // Design: the blank is centred on the design, not the canvas
        double DesignCentreX = 0, double DesignCentreY = 0)   // that centre, in source pixels
    {
        public bool Grows(int w, int h) => Size > w || Size > h;

        /// <summary>Some of the original falls outside the new canvas. Only ever background.</summary>
        public bool Crops(int w, int h) => OffsetX < 0 || OffsetY < 0 || OffsetX + w > Size || OffsetY + h > Size;
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
                                       ushort background, out long contentPixels,
                                       double? centreX = null, double? centreY = null)
    {
        // A small tolerance, not zero: the floor may carry dither or sensor noise. It can be
        // small because this runs after the level points have been applied, so a floor the
        // user flattened is already exactly uniform by the time we look at it.
        int tol = ContentTolerance(maxValue);
        double cx = centreX ?? (w - 1) / 2.0, cy = centreY ?? (h - 1) / 2.0;
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

    /// <summary>How far from the background a level has to be to count as design.</summary>
    public static int ContentTolerance(int maxValue) => Math.Max(1, maxValue / 128);

    /// <summary>
    /// The design's own centre: the middle of the box around every pixel that is not
    /// background, or null when there is none.
    ///
    /// The middle of the box rather than a centroid, because a centroid is pulled toward
    /// whichever side carries more detail, and a coin's lettering is not evenly spread. For
    /// the round designs this is for, the middle of the box is the middle of the circle.
    /// </summary>
    public static (double X, double Y)? DesignCentre(ushort[] p, int w, int h, int maxValue, ushort background)
    {
        int tol = ContentTolerance(maxValue);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            long row = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                if (Math.Abs(p[row + x] - background) <= tol) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : ((minX + maxX) / 2.0, (minY + maxY) / 2.0);
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

        if (o.Fit == FitPolicy.Design && DesignCentre(p, w, h, maxValue, background) is { } centre)
        {
            var (dx, dy) = centre;
            // The extra pixel covers rounding the offset to whole pixels: the design centre
            // can land half a pixel from the canvas centre, and nothing may be cut off for it.
            double r = ContentRadius(p, w, h, maxValue, background, out _, dx, dy) + 1;
            int side = Math.Max(1, (int)Math.Ceiling(2 * r / fraction));
            return new FitPlan(
                Size: side,
                OffsetX: (int)Math.Round((side - 1) / 2.0 - dx),
                OffsetY: (int)Math.Round((side - 1) / 2.0 - dy),
                ContentRadius: r,
                CornerRadius: cornerRadius,
                Background: background,
                ArtAcrossMm: 2 * r / side * blank,
                PixelsPerMm: side / blank,
                Recentred: true,
                DesignCentreX: dx,
                DesignCentreY: dy);
        }

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

    /// <summary>
    /// Copy the map onto a square canvas filled with one value, its top-left corner at
    /// (<paramref name="ox"/>, <paramref name="oy"/>). Negative offsets, or a canvas smaller
    /// than the map, crop: whatever falls outside is not copied. Pixels are copied, never
    /// resampled.
    /// </summary>
    public static ushort[] Pad(ushort[] src, int w, int h, int size, int ox, int oy, ushort fill)
    {
        var dst = new ushort[(long)size * size];
        if (fill != 0) Array.Fill(dst, fill);

        int sx0 = Math.Max(0, -ox), sx1 = Math.Min(w, size - ox);
        if (sx1 <= sx0) return dst;
        for (int y = 0; y < h; y++)
        {
            int ty = y + oy;
            if (ty < 0 || ty >= size) continue;
            Array.Copy(src, (long)y * w + sx0, dst, (long)ty * size + sx0 + ox, sx1 - sx0);
        }

        return dst;
    }

    /// <summary>
    /// Pixels of design that a placement leaves off the canvas. The fit is planned so this is
    /// zero - cropping is only ever meant to remove background - and it is counted rather
    /// than assumed, so the tuner can refuse the crop if it ever is not.
    /// </summary>
    public static long DesignOutside(ushort[] p, int w, int h, int size, int ox, int oy,
                                     int maxValue, ushort background)
    {
        int tol = ContentTolerance(maxValue);
        long lost = 0;
        for (int y = 0; y < h; y++)
        {
            int ty = y + oy;
            bool rowOff = ty < 0 || ty >= size;
            long row = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                int tx = x + ox;
                if (!rowOff && tx >= 0 && tx < size) continue;
                if (Math.Abs(p[row + x] - background) > tol) lost++;
            }
        }
        return lost;
    }

    private static double Sq(double v) => v * v;
}
