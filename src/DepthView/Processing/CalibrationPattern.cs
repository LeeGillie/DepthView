using System;
using System.Collections.Generic;
using System.Linq;

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
/// How a mass-loss coupon should be laid out. All dimensions in millimetres.
///
/// Defaults follow the measurement, not convenience: a 25 mm square of 3 mm stock carrying a
/// single 15 mm zone. That puts roughly 190 mg of signal at 100 um depth on a 16 g brass
/// coupon, and keeps the coupon inside a 50 g milligram scale with room to spare.
/// See docs/DEPTH-PREDICTION.md section 5.1.
/// </summary>
public sealed class MassCouponSpec
{
    /// <summary>Edge of the square coupon.</summary>
    public double CouponMm = 25;

    /// <summary>Edge of the square zone engraved at one setting.</summary>
    public double ZoneMm = 15;

    /// <summary>Stock thickness, used only to estimate coupon mass against the scale.</summary>
    public double ThicknessMm = 3;

    /// <summary>Scale capacity in grams. The common cheap milligram scale is 20 g; 50 g is advised.</summary>
    public double ScaleCapacityG = 50;

    /// <summary>
    /// Output resolution. The zone is uniform, so this only sets how finely its edge falls on
    /// a millimetre: at 40 px/mm the drawn edge is within half a pixel, 12.5 um, of the request.
    /// </summary>
    public double PixelsPerMm = 40;

    /// <summary>Rows on the worksheet: one per coupon in the series.</summary>
    public int Rows = 10;

    public string Material = "";
    public string Machine = "";
}

/// <summary>
/// Densities for the coin metals, used to turn a mass difference into volume and depth.
/// Values and grades are from docs/research/laser-ablation-baselines.md, Table M.
/// </summary>
public static class CouponMaterial
{
    /// <summary>
    /// Matches a free-text material name loosely. Returns null rather than guessing when it
    /// does not recognise the material, because a wrong density silently scales every depth.
    /// </summary>
    public static (string Name, double Density)? Lookup(string? material)
    {
        string m = (material ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return null;

        // Order matters: "C360 brass" must not fall through to the generic brass entry.
        if (m.Contains("360")) return ("brass C360", 8.50);                         // C/D, supplier sheet
        if (m.Contains("260")) return ("brass C260", 8.53);                          // C, CDA
        if (m.Contains("brass"))
            return ("brass, grade not given - C260 density assumed; use C360 for free-machining brass", 8.53);
        if (m.Contains("stainless") || m.Contains("304") || m == "ss")
            return ("stainless 304", 7.894);                                        // B, Kim ANL-75-55
        if (m.Contains("copper") || m.Contains("c110") || m == "cu")
            return ("copper C110", 8.91);                                           // C, CDA
        if (m.Contains("1050")) return ("aluminium 1050A", 2.71);                   // C, Aalco
        if (m.Contains("alumin") || m.Contains("6061") || m == "al")
            return ("aluminium 6061", 2.70);                                        // B/C, Aluminum Association
        return null;
    }
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

        /// <summary>
        /// Things the user must know before cutting this coupon. Printed ahead of everything
        /// else and repeated on the worksheet, because a coupon cut wrongly costs a blank and an
        /// hour and nobody notices until the measurements come out meaningless.
        /// </summary>
        public List<string> Warnings = new();

        /// <summary>
        /// Comb pitch actually drawn, in microns, per cell. Zero where the requested pitch
        /// cannot be represented at this resolution and the cell was left blank.
        /// </summary>
        public double[] CombPitchDrawnUm = Array.Empty<double>();

        /// <summary>Mass coupon only: the zone as drawn, after rounding to whole pixels.</summary>
        public double ZoneEdgeMm, ZoneAreaMm2;
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

            // Said out loud because the obvious thing to do with a milligram scale is weigh this
            // coupon, and it cannot work: every step is on one piece of metal, so the scale sees
            // their sum, and each step is far too small to resolve on its own anyway.
            double stepWmm = stepW / ppmm, stepHmm = (y1 - y0) / ppmm;
            r.Legend.Add($"DEPTH is for a depth gauge, microscope or tilted-coupon measurement, not the " +
                         $"scale: all {spec.WedgeSteps} steps weigh together, and each is only " +
                         $"{stepWmm:F1} x {stepHmm:F1} mm. For removal efficiency use --calibrate --mass.");
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

            // A line pair needs at least two pixels - one dark, one light - or the map simply
            // cannot express it. This used to clamp an unrepresentable pitch up to two pixels
            // and keep the original label, so a small coupon could print "25" over lines that
            // were really 57 um apart. A cell that lies about its pitch is worse than no cell:
            // it would report a spot size the machine never demonstrated. So an unrepresentable
            // cell is now left uncut, labelled "-", and named in a warning.
            r.CombPitchDrawnUm = new double[count];
            var lost = new List<double>();
            var marginal = new List<double>();

            for (int i = 0; i < count; i++)
            {
                double asked = spec.CombPitchUm[i];
                double pitchPx = asked / 1000.0 * ppmm;
                int bx = x0 + i * cellW + cellW / 10;
                int bw = cellW - 2 * (cellW / 10);

                if (pitchPx < 2)
                {
                    lost.Add(asked);
                    TinyFont.DrawCentred(px, n, n, "-", bx + bw / 2, y0 + boxH + Mm(0.4), labelScale, ink);
                    continue;
                }
                if (pitchPx < 3) marginal.Add(asked);
                r.CombPitchDrawnUm[i] = asked;

                for (int y = y0; y < y0 + boxH; y++)
                    for (int x = bx; x < bx + bw; x++)
                    {
                        bool dark = ((int)Math.Floor((x - bx) / (pitchPx / 2))) % 2 == 0;
                        Set(px, n, x, y, dark ? Black : White);
                    }

                TinyFont.DrawCentred(px, n, n, asked.ToString("0"),
                                     bx + bw / 2, y0 + boxH + Mm(0.4), labelScale, ink);
            }

            // Pixel count at which the finest requested pitch gets three pixels per pair.
            double finest = spec.CombPitchUm.Length > 0 ? spec.CombPitchUm.Min() : 0;
            int needPx = finest > 0 ? (int)Math.Ceiling(3.0 / (finest / 1000.0) * spec.BlankDiameterMm) : 0;

            if (lost.Count > 0)
                r.Warnings.Add($"SPOT comb: {string.Join(", ", lost.Select(p => p.ToString("0")))} um cannot be " +
                               $"drawn at {1000 / ppmm:F1} um/pixel and those cells are left blank. " +
                               $"Use --size {needPx} or more to include them.");
            if (marginal.Count > 0)
                r.Warnings.Add($"SPOT comb: {string.Join(", ", marginal.Select(p => p.ToString("0")))} um get under " +
                               "3 pixels per line pair, so each bar is only 1 or 2 pixels wide and the pattern is " +
                               $"uneven. Treat those cells as approximate, or use --size {needPx} or more.");
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

    /// <summary>
    /// Draws a mass-loss coupon: one uniform zone, centred, on an otherwise untouched square.
    ///
    /// <b>Why a separate coupon.</b> A milligram scale measures the total mass removed from
    /// whatever sits on the pan, so it can only ever report one number per coupon. That makes
    /// the design the reverse of the wedge: <i>one</i> setting per coupon, the settings varied
    /// <i>between</i> coupons, and each coupon weighed before and after on its own. Its one
    /// number is the removal efficiency the depth model runs on:
    /// <c>eta = dm / (rho * energy delivered)</c>.
    ///
    /// <b>Why no labels.</b> Anything engraved on the coupon is mass removed, and it would be
    /// counted as if the zone had removed it. Coupons are identified by a scribe mark or an
    /// edge notch made <i>before</i> the first weighing - never engraved, never marker ink,
    /// which the cleaning bath removes an unpredictable fraction of.
    ///
    /// <b>Why the zone is big.</b> The scale resolves one milligram wherever it sits, so the
    /// depth that one milligram represents falls as the zone grows. At the 15 mm default on
    /// brass it is about half a micron; at 5 mm it is nearly five, and the scale has lost its
    /// advantage over a dial indicator.
    /// </summary>
    public static Result BuildMassCoupon(MassCouponSpec spec)
    {
        if (spec.CouponMm <= 0 || spec.ZoneMm <= 0)
            throw new ArgumentException("coupon and zone sizes must be positive");
        if (spec.ZoneMm >= spec.CouponMm)
            throw new ArgumentException($"a {spec.ZoneMm:F1} mm zone does not fit on a {spec.CouponMm:F1} mm coupon");

        double ppmm = Math.Max(4, spec.PixelsPerMm);
        int n = (int)Math.Round(spec.CouponMm * ppmm);
        int z = (int)Math.Round(spec.ZoneMm * ppmm);
        int x0 = (n - z) / 2;

        // Two levels only, written at 8 bits: black is the zone, white is untouched. Nothing
        // between them exists in this image, so a wider format would carry nothing more.
        const ushort White = 255, Black = 0;
        var px = new ushort[(long)n * n];
        Array.Fill(px, White);
        for (int y = x0; y < x0 + z; y++)
            for (int x = x0; x < x0 + z; x++)
                px[(long)y * n + x] = Black;

        var r = new Result
        {
            Pixels = px, Width = n, Height = n,
            PixelsPerMm = ppmm, Dpi = ppmm * 25.4,
            ZoneEdgeMm = z / ppmm,
        };
        r.ZoneAreaMm2 = r.ZoneEdgeMm * r.ZoneEdgeMm;

        r.Legend.Add($"ZONE: {r.ZoneEdgeMm:F2} x {r.ZoneEdgeMm:F2} mm = {r.ZoneAreaMm2:F1} mm2, centred on a " +
                     $"{spec.CouponMm:F1} mm square coupon. Engrave the zone only; leave the border untouched.");
        r.Legend.Add("No labels on purpose: anything engraved counts as removed mass. Identify each coupon " +
                     "with a scribe mark or edge notch before the first weighing, never marker ink.");

        double border = (spec.CouponMm - r.ZoneEdgeMm) / 2;
        if (border < 2)
            r.Warnings.Add($"Only {border:F1} mm of untouched border. The border is what you handle the coupon by " +
                           "and what keeps edge burr off the zone; 2 mm or more is safer.");

        if (spec.ZoneMm < 10)
            r.Warnings.Add($"A {spec.ZoneMm:F1} mm zone is small for a milligram scale. See the sensitivity line: " +
                           "below 10 mm one scale count becomes several microns and the scale loses its " +
                           "advantage over a dial indicator. 15 mm is the recommended default.");

        var mat = CouponMaterial.Lookup(spec.Material);
        if (mat is { } m)
        {
            // rho in g/cm3 is numerically mg/mm3, which makes the arithmetic below direct.
            double umPerMg = 1000.0 / (m.Density * r.ZoneAreaMm2);
            double couponG = m.Density * spec.CouponMm * spec.CouponMm * spec.ThicknessMm / 1000.0;
            r.Legend.Add($"SENSITIVITY ({m.Name}, {m.Density:0.###} g/cm3): one milligram is {umPerMg:F2} um of average " +
                         $"depth over the zone; 1 % precision needs {umPerMg * 100:F0} um or more.");
            r.Legend.Add($"Coupon mass about {couponG:F1} g at {spec.ThicknessMm:F1} mm thick, " +
                         $"against a {spec.ScaleCapacityG:F0} g scale.");
            if (couponG > spec.ScaleCapacityG)
                r.Warnings.Add($"The coupon is about {couponG:F1} g and the scale takes {spec.ScaleCapacityG:F0} g. " +
                               "It cannot be weighed. Use a smaller or thinner coupon, or a larger scale.");
            else if (couponG > 0.8 * spec.ScaleCapacityG)
                r.Warnings.Add($"The coupon is about {couponG:F1} g, close to the scale's {spec.ScaleCapacityG:F0} g limit. " +
                               "It will weigh, but with little headroom if you cut thicker stock or add a fixture.");
        }
        else
        {
            r.Warnings.Add(spec.Material.Length == 0
                ? "No --material given, so no sensitivity or coupon mass could be worked out. The worksheet leaves density blank."
                : $"Material \"{spec.Material}\" is not one of the known coin metals (brass, copper, stainless 304, " +
                  "aluminium), so no sensitivity could be worked out. Enter its density on the worksheet.");
        }

        return r;
    }

    private static void Set(ushort[] p, int n, int x, int y, ushort v)
    {
        if (x < 0 || y < 0 || x >= n || y >= n) return;
        p[(long)y * n + x] = v;
    }
}
