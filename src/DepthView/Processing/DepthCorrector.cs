using System;
using System.Collections.Generic;
using DepthView.Imaging;

namespace DepthView.Processing;

/// <summary>What a correction did, so the change can be stated in numbers rather than trusted.</summary>
public sealed class CorrectionReport
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
}

/// <summary>
/// Applies a correction to a depth map.
///
/// Order matters and is deliberate: levels first, because everything downstream is expressed
/// in the corrected range; then the rim, which must be able to paint over whatever the levels
/// produced; then quantisation, so the slice count is the last word on how many depths exist;
/// then inversion, which is purely a convention flip at the very end.
///
/// Nothing here ever writes over the source. The caller supplies the destination.
/// </summary>
public static class DepthCorrector
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
                                 CorrectionOptions o, out CorrectionReport report)
    {
        report = new CorrectionReport { MaxValue = maxValue };
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

        // ---- rim --------------------------------------------------------
        if (o.AddRim && o.RimRadius > 0)
            ApplyRim(source, outp, width, height, maxValue, o, report);

        // ---- quantise ---------------------------------------------------
        if (o.Slices > 1)
            Quantise(outp, maxValue, o.Slices, o.Dither, width);

        // ---- invert -----------------------------------------------------
        if (o.Invert)
            for (int i = 0; i < outp.Length; i++)
                outp[i] = (ushort)(maxValue - outp[i]);

        for (int i = 0; i < source.Length; i++)
            if (source[i] != outp[i]) report.Changed++;

        return outp;
    }

    /// <summary>
    /// Paints an untouched ring at the edge and ramps into it.
    ///
    /// The ramp blends the existing value toward white as a function of radius rather than
    /// heading for one fixed level. That way it meets the artwork exactly at the inner edge
    /// at every angle - no seam where the design happens to sit deeper on one side - and it
    /// can only ever lighten a pixel, so it can never cut somewhere the original did not.
    /// </summary>
    private static void ApplyRim(ushort[] source, ushort[] outp, int width, int height,
                                 int maxValue, CorrectionOptions o, CorrectionReport report)
    {
        double cx = o.RimCentreX ?? (width - 1) / 2.0;
        double cy = o.RimCentreY ?? (height - 1) / 2.0;
        double outer = o.RimRadius;
        double inner = Math.Max(0, outer - Math.Max(0, o.RimRamp));

        // "Content" means clearly not background: above a fifth of the range. Used only to
        // report what the rim costs, never to decide anything.
        int contentFloor = maxValue / 5;
        long totalContent = 0;
        for (int i = 0; i < source.Length; i++) if (source[i] > contentFloor) totalContent++;

        for (int y = 0; y < height; y++)
        {
            double dy = y - cy;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < inner) continue;

                int idx = row + x;
                bool hadContent = source[idx] > contentFloor;

                if (r >= outer)
                {
                    if (outp[idx] != maxValue) report.RimPixels++;
                    if (hadContent) report.RimClipped++;
                    outp[idx] = (ushort)maxValue;
                    continue;
                }

                double t = (r - inner) / (outer - inner);
                t = t * t * (3 - 2 * t);                       // smoothstep
                int v = outp[idx];
                outp[idx] = (ushort)Math.Round(v + (maxValue - v) * t);
                if (hadContent && t > 0.5) report.RimClipped++;
            }
        }

        report.RimClippedFraction = totalContent > 0 ? (double)report.RimClipped / totalContent : 0;

        // If the rim eats into the design, say by how much the art would have to shrink to
        // clear it: the furthest content from the centre, against the ramp's inner edge.
        if (report.RimClipped > 0 && inner > 0)
        {
            double furthest = 0;
            for (int y = 0; y < height; y++)
            {
                double dy = y - cy;
                int row = y * width;
                for (int x = 0; x < width; x++)
                    if (source[row + x] > contentFloor)
                    {
                        double dx = x - cx;
                        double r = Math.Sqrt(dx * dx + dy * dy);
                        if (r > furthest) furthest = r;
                    }
            }
            if (furthest > inner) report.SuggestedScale = inner / furthest;
        }

        report.Summary =
            report.RimClipped == 0
                ? "The rim sits clear of the design."
                : $"The rim overlaps {report.RimClipped:N0} pixels of design "
                + $"({report.RimClippedFraction * 100:F2}% of it). Scaling the art to "
                + $"{report.SuggestedScale * 100:F0}% would clear it.";
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
