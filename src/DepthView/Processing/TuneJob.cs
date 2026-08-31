using System;
using System.IO;
using DepthView.Imaging;

namespace DepthView.Processing;

/// <summary>
/// The steps between "here are the options" and "there is a file on disk", plus the cheap
/// histogram arithmetic the interactive dialog needs to keep its numbers live.
///
/// This exists so the command line and the Tune window run the same code rather than two
/// implementations that agree today. A tuned file written from the dialog and one written
/// by --tune with the same settings are the same bytes, and that is worth a shared class.
/// </summary>
public static class TuneJob
{
    /// <summary>
    /// Move the samples into the container the output asks for.
    ///
    /// Written at the source's own precision unless told otherwise: silently taking a 16-bit
    /// map down to 8 bits on the way out would be the exact fault this program reports.
    /// </summary>
    public static ushort[] ScaleForOutput(ushort[] tuned, int maxValue, int outBits)
    {
        int target = outBits == 8 ? 255 : 65535;
        if (maxValue == target) return tuned;

        var scaled = new ushort[tuned.Length];
        for (int i = 0; i < tuned.Length; i++)
            scaled[i] = (ushort)Math.Round(tuned[i] / (double)maxValue * target);
        return scaled;
    }

    /// <summary>
    /// Write a tuned map, stamped with the settings that produced it.
    ///
    /// The tEXt notes are not decoration. A depth map that has been through a tool and then
    /// sits in a folder for six months is otherwise indistinguishable from the original, and
    /// the one question anyone asks of it later is what was done to it.
    /// </summary>
    public static void WriteTuned(string path, ushort[] tuned, int width, int height,
                                  int maxValue, TuningOptions o, string? sourceName)
    {
        using var fs = File.Create(path);
        WriteTuned(fs, tuned, width, height, maxValue, o, sourceName);
    }

    /// <summary>
    /// The same, to an open stream, so the window can write through a file picker's handle
    /// without needing a real path - which is the one thing a sandboxed picker may not give.
    /// </summary>
    public static void WriteTuned(Stream output, ushort[] tuned, int width, int height,
                                  int maxValue, TuningOptions o, string? sourceName)
    {
        int outBits = o.OutputBitDepth == 8 ? 8 : 16;
        var scaled = ScaleForOutput(tuned, maxValue, outBits);

        var notes = new[]
        {
            ("Software", $"DepthView {BuildInfo.Version}"),
            ("Source",   sourceName ?? "unknown"),
            ("Comment",  $"black={o.BlackPoint} white={o.WhitePoint} stretch={o.Stretch} " +
                         $"rim={(o.AddRim ? $"r{o.RimRadius:F0}/ramp{o.RimRamp:F0}" : "off")} " +
                         $"slices={o.Slices} dither={o.Dither} invert={o.Invert}"),
        };

        PngEncoder.WriteGrey(output, scaled, width, height, outBits, o.Dpi, notes);
    }

    /// <summary>
    /// The rim as its own image, for running the field on separate laser settings.
    /// White is the engraved area, black is the rim to leave alone.
    /// </summary>
    public static void WriteRimMask(string path, int width, int height, TuningOptions o)
    {
        double cx = o.RimCentreX ?? (width - 1) / 2.0;
        double cy = o.RimCentreY ?? (height - 1) / 2.0;
        var mask = new ushort[(long)width * height];

        for (int y = 0; y < height; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                mask[y * width + x] = Math.Sqrt(dx * dx + dy * dy) >= o.RimRadius ? (ushort)0 : (ushort)255;
            }
        }

        // A mask is two states, so 8 bits is honest and a fifth of the size.
        PngEncoder.WriteGrey(path, mask, width, height, 8);
    }

    // ------------------------------------------------------------------ live arithmetic

    /// <summary>
    /// Put the source histogram through the same level maths <see cref="DepthTuner"/> applies
    /// to pixels, and hand back the histogram the tuned file will have.
    ///
    /// This is how the dialog stays live on a 4096 x 4096 map. Every level question - how many
    /// depths survive at a pass count, how much of the range is used, how many pixels the two
    /// points absorb - is answerable from 65,536 bins instead of 16.8 million pixels, and the
    /// answer is exact rather than sampled, because it is the whole population counted once.
    ///
    /// The rim is the one thing it cannot model: that is geometry, not levels. The dialog gets
    /// those numbers from the downsampled preview instead, where a fraction is all that is
    /// wanted anyway.
    /// </summary>
    public static long[] MapHistogram(long[] source, int maxValue, TuningOptions o,
                                      out long flattened, out long lifted)
    {
        flattened = lifted = 0;
        var dest = new long[maxValue + 1];

        int black = Math.Clamp(o.BlackPoint, 0, maxValue);
        int white = Math.Clamp(o.WhitePoint <= 0 ? maxValue : o.WhitePoint, 0, maxValue);
        if (white <= black) white = Math.Min(maxValue, black + 1);
        double span = white - black;

        double step = o.Slices > 1 ? (double)maxValue / (o.Slices - 1) : 0;
        int limit = Math.Min(source.Length - 1, maxValue);

        for (int v = 0; v <= limit; v++)
        {
            long c = source[v];
            if (c == 0) continue;

            int outv;
            if (v <= black) { outv = 0; if (v != 0) flattened += c; }
            else if (v >= white) { outv = maxValue; if (v != maxValue) lifted += c; }
            else outv = o.Stretch ? (int)Math.Round((v - black) / span * maxValue) : v;

            if (step > 0)
                outv = (int)Math.Clamp(Math.Round(outv / step) * step, 0, maxValue);

            if (o.Invert) outv = maxValue - outv;

            dest[Math.Clamp(outv, 0, maxValue)] += c;
        }

        return dest;
    }

    /// <summary>
    /// Distinct depths a histogram yields at a given pass count, and how many passes then
    /// repeat a depth that already exists.
    ///
    /// Both limits are real: you cannot resolve more depths than you have passes, and you
    /// cannot resolve more than the file holds once reduced to that many levels.
    /// </summary>
    public static (int Distinct, int Wasted) DepthsAt(long[] hist, int maxValue, int passes)
    {
        if (passes <= 1 || maxValue <= 0) return (0, Math.Max(0, passes));

        var seen = new bool[passes];
        int distinct = 0;
        int limit = Math.Min(hist.Length - 1, maxValue);

        for (int v = 0; v <= limit; v++)
        {
            if (hist[v] == 0) continue;
            int q = (int)Math.Round((double)v / maxValue * (passes - 1));
            q = Math.Clamp(q, 0, passes - 1);
            if (!seen[q]) { seen[q] = true; distinct++; }
        }

        return (distinct, Math.Max(0, passes - distinct));
    }

    /// <summary>Occupied span and level count of a histogram, in one pass.</summary>
    public static (int Min, int Max, int Unique) Span(long[] hist)
    {
        int min = -1, max = -1, unique = 0;
        for (int v = 0; v < hist.Length; v++)
        {
            if (hist[v] == 0) continue;
            if (min < 0) min = v;
            max = v;
            unique++;
        }
        return (min < 0 ? 0 : min, max < 0 ? 0 : max, unique);
    }

    /// <summary>Share of the container the occupied levels span. 1.0 means edge to edge.</summary>
    public static double RangeUse(long[] hist, int maxValue)
    {
        var (min, max, unique) = Span(hist);
        return unique == 0 || maxValue <= 0 ? 0 : (max - min + 1) / (double)(maxValue + 1);
    }
}
