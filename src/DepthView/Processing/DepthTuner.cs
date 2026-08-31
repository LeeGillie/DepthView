using System;
using System.Collections.Generic;
using DepthView.Imaging;

namespace DepthView.Processing;

/// <summary>What a correction did, so the change can be stated in numbers rather than trusted.</summary>
public sealed class TuningReport
{
    public int MaxValue;
    public long FlattenedToBlack;      // pixels absorbed by the black point
    public long LiftedToWhite;         // pixels absorbed by the white point
    public long RimPixels;             // pixels the rim ring painted out
    public long RimClipped;            // of those, ones that held real content
    public double RimClippedFraction;  // as a share of all content in the image
    public double SuggestedScale = 1;  // shrink the art by this to make the rim fit cleanly
    public long Changed;               // pixels whose value moved at all
    public string Summary = "";

    /// <summary>
    /// Size of the result. Not always the size of the input: fitting the map inside a rim
    /// grows the canvas, so the caller has to write the file at these dimensions rather than
    /// the ones it handed in.
    /// </summary>
    public int OutWidth, OutHeight;

    /// <summary>Set when the canvas was grown to make the design clear the rim.</summary>
    public DepthCanvas.FitPlan? Fit;
}

/// <summary>
/// Applies a correction to a depth map.
///
/// Order matters and is deliberate: levels first, because everything downstream is expressed
/// in the corrected range; then the fit, which decides how big the canvas has to be for the
/// design to clear the rim; then the rim itself, which must be able to paint over whatever the
/// levels produced; then quantisation, so the slice count is the last word on how many depths
/// exist; then inversion, which is purely a convention flip at the very end.
///
/// Nothing here ever writes over the source. The caller supplies the destination.
/// </summary>
public static class DepthTuner
{
    /// <summary>Pull a single grey plane out of an image, at its native precision.</summary>
    public static ushort[] ExtractGrey(ImageData img)
    {
        if (img.Samples is null)
            throw new NotSupportedException(
                "This image decoded to floating point. Correction works on integer depth maps; " +
                "convert the float map to 16-bit PNG first.");

        long count = img.PixelCount;
        var grey = new ushort[count];
        int ch = Math.Max(1, img.Channels);
        var s = img.Samples;
        for (long i = 0; i < count; i++) grey[i] = s[i * ch];
        return grey;
    }

    public static ushort[] Apply(ushort[] source, int width, int height, int maxValue,
                                 TuningOptions o, out TuningReport report)
    {
        report = new TuningReport { MaxValue = maxValue, OutWidth = width, OutHeight = height };
        var outp = new ushort[source.Length];

        int black = Math.Clamp(o.BlackPoint, 0, maxValue);
        int white = Math.Clamp(o.WhitePoint <= 0 ? maxValue : o.WhitePoint, 0, maxValue);
        if (white <= black) white = Math.Min(maxValue, black + 1);
        double span = white - black;

        // ---- levels -----------------------------------------------------
        for (int i = 0; i < source.Length; i++)
        {
            int v = source[i];
            if (v <= black) { outp[i] = 0; if (v != 0) report.FlattenedToBlack++; continue; }
            if (v >= white) { outp[i] = (ushort)maxValue; if (v != maxValue) report.LiftedToWhite++; continue; }

            outp[i] = o.Stretch
                ? (ushort)Math.Round((v - black) / span * maxValue)
                : (ushort)v;
        }

        for (int i = 0; i < source.Length; i++)
            if (source[i] != outp[i]) report.Changed++;

        // The value that means "no passes at all, leave the metal alone".
        //
        // Inversion happens at the very end, so anything that must come out untouched has to
        // be written as its mirror now. Without this, --invert together with a rim cut the rim
        // to full depth - the one part of a coin blank nobody wants the laser to reach.
        ushort untouched = o.Invert ? (ushort)0 : (ushort)maxValue;

        int w = width, h = height;

        // The level the design sits on, measured once, here, and used for every question that
        // follows: how far out the artwork reaches, what the new space should be filled with,
        // and what counts as design when reporting what the rim covered.
        //
        // Measured before any padding, deliberately. It comes from the border, and the border
        // is only reliably background while it is still the original file's own edge - once
        // padding is in place the border is whatever we just put there, and asking it what the
        // background is would return our own answer.
        ushort background = DepthCanvas.BackgroundLevel(outp, w, h);

        // The map at its original extent, kept across the padding step. Everything downstream
        // that asks "is this artwork" asks this rather than the padded buffer, because only
        // the original rectangle can contain artwork.
        ushort[] design = outp;
        int dw = w, dh = h;

        // ---- fit --------------------------------------------------------
        // Grow the canvas until the design clears the rim, instead of letting the rim paint
        // over it. The blank is a fixed 40 mm whatever we do, so a larger canvas means the
        // same artwork spans fewer millimetres of it - the same outcome as scaling the art
        // down, reached without resampling a single pixel.
        if (o.AddRim && o.Fit != FitPolicy.None && o.BlankDiameterMm is > 0)
        {
            var plan = DepthCanvas.Plan(outp, w, h, maxValue, o, background);
            if (plan.Size > 0 && plan.Grows(w, h))
            {
                // What the new ring gets cut to, and it is not a cosmetic choice.
                //
                // Matching the background keeps the field continuous, with no step where the
                // original file ended, and it is self-correcting across conventions: art on a
                // cut-away floor extends the floor, art on an untouched field extends that.
                // The cost is that on a cut-away floor it means engraving the whole new ring
                // to full depth, which is real time on the machine and real depth budget.
                //
                // Leaving it untouched costs nothing to cut, but only looks right when the
                // design's background is already near untouched - otherwise the boundary of
                // the source image shows up as a square step around the artwork.
                ushort fill = o.PadWith == PadFill.Untouched ? untouched : background;
                outp = DepthCanvas.Pad(outp, w, h, plan.Size, plan.OffsetX, plan.OffsetY, fill);
                w = h = plan.Size;
                report.Fit = plan;
                report.OutWidth = w;
                report.OutHeight = h;

                // Every millimetre figure was resolved against the old canvas and is now wrong.
                // The written resolution especially: a pHYs chunk describing the original size
                // would place the padded map at the wrong scale, which is a worse failure than
                // writing none at all. Only rewritten when one was going to be written.
                double ppmm = plan.PixelsPerMm;
                o.RimRadius = Math.Max(1, w / 2.0 - (o.RimWidthMm ?? 0) * ppmm);
                o.RimRamp = (o.RimRampMm ?? 0) * ppmm;
                if (o.Dpi is not null) o.Dpi = ppmm * 25.4;
            }
        }

        // ---- rim --------------------------------------------------------
        if (o.AddRim && o.RimRadius > 0)
            ApplyRim(outp, w, h, design, dw, dh, maxValue, untouched, background, o, report);

        // ---- quantise ---------------------------------------------------
        if (o.Slices > 1)
            Quantise(outp, maxValue, o.Slices, o.Dither, w);

        // ---- invert -----------------------------------------------------
        if (o.Invert)
            for (int i = 0; i < outp.Length; i++)
                outp[i] = (ushort)(maxValue - outp[i]);

        return outp;
    }

    /// <summary>
    /// Paints an untouched ring at the edge and ramps into it.
    ///
    /// The ramp blends the existing value toward untouched as a function of radius rather than
    /// heading for one fixed level. That way it meets the artwork exactly at the inner edge
    /// at every angle - no seam where the design happens to sit deeper on one side - and it
    /// can only ever move a pixel toward untouched, so it can never cut somewhere the original
    /// did not.
    ///
    /// <para><paramref name="design"/> is the map at its original extent, before any padding,
    /// and it is the only thing consulted about what counts as artwork. That distinction is
    /// what keeps the accounting honest: padding puts our own fill around the edge, and a
    /// content test run over the padded buffer ends up measuring whatever this code just wrote
    /// there - reporting the field as clipped design with a background fill, and the padding
    /// itself as clipped design with an untouched one. The design lives in the original
    /// rectangle; nothing outside it can be artwork. Padding is centred, so a radius measured
    /// from the original's centre is the same distance in the padded canvas.</para>
    /// </summary>
    private static void ApplyRim(ushort[] buf, int width, int height,
                                 ushort[] design, int dw, int dh,
                                 int maxValue, ushort untouched, ushort background,
                                 TuningOptions o, TuningReport report)
    {
        double cx = o.RimCentreX ?? (width - 1) / 2.0;
        double cy = o.RimCentreY ?? (height - 1) / 2.0;
        double outer = o.RimRadius;
        double inner = Math.Max(0, outer - Math.Max(0, o.RimRamp));

        // The background arrives from the caller, measured on the original extent while the
        // border was still the file's own edge rather than our padding.
        int tol = Math.Max(1, maxValue / 128);

        // A ramp covers a pixel gradually; call it covered past the halfway point of the
        // blend, which is also exactly "past the rim" when there is no ramp at all.
        double coveredFrom = (inner + outer) / 2;

        long totalContent = 0;
        double furthestContent = 0;
        double dcx = (dw - 1) / 2.0, dcy = (dh - 1) / 2.0;

        for (int y = 0; y < dh; y++)
        {
            double dy = y - dcy;
            long row = (long)y * dw;
            for (int x = 0; x < dw; x++)
            {
                if (Math.Abs(design[row + x] - background) <= tol) continue;
                totalContent++;

                double dx = x - dcx;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > furthestContent) furthestContent = r;
                if (r >= coveredFrom) report.RimClipped++;
            }
        }

        for (int y = 0; y < height; y++)
        {
            double dy = y - cy;
            long row = (long)y * width;
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < inner) continue;

                long idx = row + x;

                if (r >= outer)
                {
                    if (buf[idx] != untouched) report.RimPixels++;
                    buf[idx] = untouched;
                    continue;
                }

                double t = (r - inner) / (outer - inner);
                t = t * t * (3 - 2 * t);                       // smoothstep
                int v = buf[idx];
                buf[idx] = (ushort)Math.Round(v + (untouched - v) * t);
            }
        }

        report.RimClippedFraction = totalContent > 0 ? (double)report.RimClipped / totalContent : 0;

        // If the rim eats into the design, say by how much the art would have to shrink to
        // clear it: the furthest content from the centre, against the ramp's inner edge.
        if (report.RimClipped > 0 && inner > 0 && furthestContent > inner)
            report.SuggestedScale = inner / furthestContent;

        report.Summary = report.RimClipped == 0
            ? report.Fit is { } fit
                ? $"The canvas was grown to {fit.Size:N0} px so the design clears the rim; "
                + $"the artwork now spans {fit.ArtAcrossMm:F1} mm of the blank."
                : "The rim sits clear of the design."
            : $"The rim overlaps {report.RimClipped:N0} pixels of design "
            + $"({report.RimClippedFraction * 100:F2}% of it). Scaling the art to "
            + $"{report.SuggestedScale * 100:F0}% would clear it, or grow the canvas instead "
            + "and keep every pixel.";
    }

    /// <summary>
    /// Reduce to a fixed number of depths, optionally scattering the boundaries.
    ///
    /// A slicer thresholds hard, so on a smooth surface every boundary lands as a contour
    /// ring. Ordered dithering offsets each pixel by a fraction of a step before rounding,
    /// which turns a hard ring into a stippled transition - the same trick that hides banding
    /// in a gradient, doing the same job on metal.
    /// </summary>
    private static void Quantise(ushort[] p, int maxValue, int slices, bool dither, int width)
    {
        double step = (double)maxValue / (slices - 1);

        // 8x8 ordered (Bayer) matrix, normalised to -0.5..+0.5 of a step.
        int[,] bayer =
        {
            {  0, 32,  8, 40,  2, 34, 10, 42 }, { 48, 16, 56, 24, 50, 18, 58, 26 },
            { 12, 44,  4, 36, 14, 46,  6, 38 }, { 60, 28, 52, 20, 62, 30, 54, 22 },
            {  3, 35, 11, 43,  1, 33,  9, 41 }, { 51, 19, 59, 27, 49, 17, 57, 25 },
            { 15, 47,  7, 39, 13, 45,  5, 37 }, { 63, 31, 55, 23, 61, 29, 53, 21 },
        };

        for (int i = 0; i < p.Length; i++)
        {
            double v = p[i];
            if (dither)
            {
                int x = i % width, y = i / width;
                v += (bayer[y & 7, x & 7] / 64.0 - 0.5) * step;
            }
            int q = (int)Math.Round(v / step);
            p[i] = (ushort)Math.Clamp(q * step, 0, maxValue);
        }
    }

    /// <summary>
    /// Sensible starting points for the two level markers, from percentiles of the histogram.
    /// Percentiles rather than min and max because a handful of stray pixels at either
    /// extreme is common in generated art, and one of them is enough to make a min/max
    /// stretch do nothing at all.
    /// </summary>
    public static (int Black, int White) SuggestLevels(long[] histogram, double lowPct = 0.1,
                                                       double highPct = 99.9)
    {
        long total = 0;
        foreach (long c in histogram) total += c;
        if (total == 0) return (0, histogram.Length - 1);

        long lowTarget = (long)(total * lowPct / 100.0);
        long highTarget = (long)(total * highPct / 100.0);
        int black = 0, white = histogram.Length - 1;

        long run = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            run += histogram[i];
            if (run >= lowTarget) { black = i; break; }
        }
        run = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            run += histogram[i];
            if (run >= highTarget) { white = i; break; }
        }
        if (white <= black) white = Math.Min(histogram.Length - 1, black + 1);
        return (black, white);
    }
}
