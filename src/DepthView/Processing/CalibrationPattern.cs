using System;
using System.Collections.Generic;

namespace DepthView.Processing;

/// <summary>How a calibration coupon should be laid out. All dimensions in millimetres.</summary>
public sealed class CalibrationSpec
{
    /// <summary>Blank diameter. The pattern is fitted inside this, less the rim.</summary>
    public double BlankDiameterMm = 40;

    /// <summary>Rim to leave untouched at the edge.</summary>
    public double RimMm = 1.0;

    /// <summary>Extra clearance inside the rim, so nothing important sits on the curve.</summary>
    public double MarginMm = 1.0;

    /// <summary>Output resolution. 4096 px on a 40 mm blank is about 10 um per pixel.</summary>
    public int Pixels = 4096;

    /// <summary>Steps in the depth wedge. Each is one measurement.</summary>
    public int WedgeSteps = 16;

    /// <summary>Ramp widths to try, in mm. Zero means a hard step.</summary>
    public double[] RampsMm = { 0, 0.05, 0.1, 0.2, 0.4 };

    /// <summary>Line-pair pitches to try, in microns.</summary>
    public double[] CombPitchUm = { 200, 150, 100, 70, 50, 35, 25 };

    /// <summary>
    /// How deep the labels cut, as a fraction of full depth.
    ///
    /// Shallow on purpose. Labels only have to be legible, and cutting them to full depth on
    /// a 1 mm job means over a millimetre of deep engraving spent on text - slow, and it
    /// dumps a lot of heat into a small coupon for no measurement value. A light mark reads
    /// clearly on brass or steel and costs almost nothing.
    /// </summary>
    public double LabelDepthFraction = 0.18;

    public string Material = "";
    public string Machine = "";
}

/// <summary>
/// Draws a calibration coupon: one engraving that answers the three questions this tool
/// otherwise has to guess at.
///
/// <b>Why this exists.</b> Everything DepthView says about physical outcomes - microns per
/// pass, whether a wall angle is achievable, whether a map out-resolves the beam - depends
/// on the machine and the material. Hard-coding one laser's numbers would quietly mislead
/// everyone using a different one, and asking users for figures they do not have is no
/// better. So the tool emits a pattern, you engrave it on your machine and your material,
/// measure it, and every claim afterwards is calibrated rather than assumed.
///
/// <b>Convention:</b> black is deepest, white is untouched. The field is left white so only
/// the test features are cut - that keeps the coupon quick and leaves the original surface
/// as the datum to measure depths against.
/// </summary>
public static class CalibrationPattern
{
    public sealed class Result
    {
        public ushort[] Pixels = Array.Empty<ushort>();
        public int Width, Height;
        public double PixelsPerMm;
        public double Dpi;
        /// <summary>What to measure, in the order the features appear.</summary>
        public List<string> Legend = new();
    }

    public static Result Build(CalibrationSpec spec)
    {
        int n = Math.Max(256, spec.Pixels);
        double ppmm = n / spec.BlankDiameterMm;
        const ushort White = 65535, Black = 0;
        // Labels are marked, not excavated: legible without spending the job on text.
        ushort ink = (ushort)Math.Round(65535 * (1 - Math.Clamp(spec.LabelDepthFraction, 0.02, 1.0)));

        var px = new ushort[(long)n * n];
        Array.Fill(px, White);                       // untouched everywhere to begin with

        var r = new Result
        {
            Pixels = px, Width = n, Height = n,
            PixelsPerMm = ppmm, Dpi = ppmm * 25.4,
        };

        double cx = (n - 1) / 2.0, cy = (n - 1) / 2.0;
        double usableR = (spec.BlankDiameterMm / 2 - spec.RimMm - spec.MarginMm) * ppmm;

        int Mm(double mm) => (int)Math.Round(mm * ppmm);
        int labelScale = Math.Max(1, Mm(0.9) / 7);   // aim for roughly 0.9 mm tall text

        // Bands are placed by distance from the centre. A circle is widest in the middle, so
        // the widest feature - the depth wedge - goes there and the others sit above and below.
        // Half-width available at a given offset is sqrt(R^2 - d^2); everything below is sized
        // to stay inside that.

        // ---- depth wedge, across the middle -----------------------------
        // The measurement that matters most: brass does not ablate linearly as the pocket
        // deepens, so a linear depth map does not give linear depth. Measuring each step is
        // what turns that from a known problem into a correction.
        {
            double bandTop = -3.5, bandBot = 3.5;                        // mm from centre
            double half = Math.Sqrt(usableR * usableR - Math.Pow(Mm(Math.Max(Math.Abs(bandTop), Math.Abs(bandBot))), 2));
            double wedgeW = Math.Min(half * 2 * 0.96, Mm(32));
            int x0 = (int)(cx - wedgeW / 2), y0 = (int)(cy + Mm(bandTop)), y1 = (int)(cy + Mm(bandBot));
            int stepW = (int)(wedgeW / spec.WedgeSteps);

            for (int i = 0; i < spec.WedgeSteps; i++)
            {
                // Step 0 is fully deep, the last step is untouched: the full commanded range.
                ushort level = (ushort)Math.Round(i / (double)(spec.WedgeSteps - 1) * 65535);
                int sx = x0 + i * stepW;
                for (int y = y0; y < y1; y++)
                    for (int x = sx; x < sx + stepW; x++)
                        Set(px, n, x, y, level);
            }

            // Number every fourth step, in the untouched field below the wedge, engraved dark
            // so it is legible against unengraved metal.
            for (int i = 0; i < spec.WedgeSteps; i += 4)
                TinyFont.DrawCentred(px, n, n, (i + 1).ToString(),
                                     x0 + i * stepW + stepW / 2, y1 + Mm(0.4), labelScale, ink);

            TinyFont.DrawCentred(px, n, n, "DEPTH", (int)cx, y0 - Mm(1.5), labelScale, ink);
            r.Legend.Add($"DEPTH: {spec.WedgeSteps} steps, left fully deep to right untouched. " +
                         "Measure each step against the unengraved field. Steps are numbered every 4.");
        }

        // ---- ramp row, above ---------------------------------------------
        // Equal depth, different ramp widths. Whichever comes out clean is the narrowest
        // shoulder this machine can actually hold - a measurement rather than an opinion.
        {
            double bandC = -9.5;
            int count = spec.RampsMm.Length;
            double half = Math.Sqrt(Math.Max(1, usableR * usableR - Math.Pow(Mm(bandC + 3), 2)));
            double rowW = Math.Min(half * 2 * 0.94, Mm(26));
            int cellW = (int)(rowW / count);
            int boxH = Mm(4.0);
            int x0 = (int)(cx - rowW / 2), y0 = (int)(cy + Mm(bandC) - boxH / 2);

            for (int i = 0; i < count; i++)
            {
                double rampPx = spec.RampsMm[i] * ppmm;
                int bx = x0 + i * cellW + cellW / 10;
                int bw = cellW - 2 * (cellW / 10);

                for (int y = y0; y < y0 + boxH; y++)
                    for (int x = bx; x < bx + bw; x++)
                    {
                        // Distance to the nearest edge of this pocket, in pixels.
                        double d = Math.Min(Math.Min(x - bx, bx + bw - 1 - x),
                                            Math.Min(y - y0, y0 + boxH - 1 - y));
                        double t = rampPx <= 0 ? 1 : Math.Clamp(d / rampPx, 0, 1);
                        t = t * t * (3 - 2 * t);
                        Set(px, n, x, y, (ushort)Math.Round(65535 * (1 - t)));
                    }

                string label = spec.RampsMm[i] <= 0 ? "0" : spec.RampsMm[i].ToString("0.00");
                TinyFont.DrawCentred(px, n, n, label, bx + bw / 2, y0 + boxH + Mm(0.4), labelScale, ink);
            }
            TinyFont.DrawCentred(px, n, n, "RAMP", (int)cx, y0 - Mm(1.5), labelScale, ink);
            r.Legend.Add("RAMP: pockets of equal depth with ramps in mm as labelled (0 = hard step). " +
                         "The narrowest that comes out clean is your minimum usable shoulder.");
        }

        // ---- resolution comb, below --------------------------------------
        // Where the line pairs stop resolving is the effective spot on this material, which
        // is often not what the spec sheet says.
        {
            double bandC = 9.5;
            int count = spec.CombPitchUm.Length;
            double half = Math.Sqrt(Math.Max(1, usableR * usableR - Math.Pow(Mm(bandC + 3), 2)));
            double rowW = Math.Min(half * 2 * 0.94, Mm(26));
            int cellW = (int)(rowW / count);
            int boxH = Mm(3.5);
            int x0 = (int)(cx - rowW / 2), y0 = (int)(cy + Mm(bandC) - boxH / 2);

            for (int i = 0; i < count; i++)
            {
                double pitchPx = spec.CombPitchUm[i] / 1000.0 * ppmm;
                int bx = x0 + i * cellW + cellW / 10;
                int bw = cellW - 2 * (cellW / 10);
                if (pitchPx < 2) pitchPx = 2;        // below two pixels the map cannot say it

                for (int y = y0; y < y0 + boxH; y++)
                    for (int x = bx; x < bx + bw; x++)
                    {
                        bool dark = ((int)Math.Floor((x - bx) / (pitchPx / 2))) % 2 == 0;
                        Set(px, n, x, y, dark ? Black : White);
                    }

                TinyFont.DrawCentred(px, n, n, spec.CombPitchUm[i].ToString("0"),
                                     bx + bw / 2, y0 + boxH + Mm(0.4), labelScale, ink);
            }
            TinyFont.DrawCentred(px, n, n, "SPOT UM", (int)cx, y0 - Mm(1.5), labelScale, ink);
            r.Legend.Add("SPOT: line pairs at the labelled pitch in microns. The finest pitch still " +
                         "resolved as separate lines is your effective spot size on this material.");
        }

        // ---- rim ----------------------------------------------------------
        // Left untouched, both to match how a real job protects the blank's rim and to keep
        // an original surface on the coupon to measure depths against.
        double rimR = (spec.BlankDiameterMm / 2 - spec.RimMm) * ppmm;
        for (int y = 0; y < n; y++)
        {
            double dy = y - cy;
            for (int x = 0; x < n; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy >= rimR * rimR) px[(long)y * n + x] = White;
            }
        }

        return r;
    }

    private static void Set(ushort[] p, int n, int x, int y, ushort v)
    {
        if (x < 0 || y < 0 || x >= n || y >= n) return;
        p[(long)y * n + x] = v;
    }
}
