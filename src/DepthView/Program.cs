using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using DepthView.Analysis;
using DepthView.Imaging;
using DepthView.Processing;
using SixLabors.ImageSharp;   // for the SaveAsPng extension on the headless render path

namespace DepthView;

internal static class Program
{
    /// <summary>File passed on the command line, loaded once the window opens.</summary>
    public static string? StartupFile;

    /// <summary>Open the relief preview straight away, alongside the analysis window.</summary>
    public static bool StartupRelief;

    /// <summary>Opens the tuning dialog with the window, so its layout can be screenshotted too.</summary>
    public static bool StartupTune;

    /// <summary>
    /// Rim geometry and pass count to open the tuning dialog already set to, from the same
    /// flags --tune uses. Two reasons this is worth having: a starting point can be scripted
    /// for a blank you cut often, and the rim case becomes screenshottable, which is how the
    /// layout of a dialog gets checked on screens nobody here owns.
    /// </summary>
    public static double? StartupBlankMm, StartupRimMm, StartupRampMm;
    public static int? StartupPasses;

    /// <summary>Open the About box straight away. Exists so its screenshot is reproducible too.</summary>
    public static bool StartupAbout;

    /// <summary>Open the About box showing the licence page rather than the credit roll.</summary>
    public static bool StartupLicence;

    /// <summary>When set, capture the window to this PNG once it has settled, then exit.</summary>
    public static string? ScreenshotPath;

    /// <summary>Overrides how long to wait before the capture. Useful for catching the credit roll mid-scroll.</summary>
    public static int? ScreenshotDelayMs;

    /// <summary>
    /// Forces the main window to open at this size. Exists because layout faults only appear
    /// at sizes the developer's monitor never produces: the buttons and the verdict card were
    /// once clipped off the bottom on a 1024x768 screen and nobody could have seen it on a
    /// 4K display. Being able to say "show me the window at 900x560" makes that testable.
    /// </summary>
    public static int? WindowWidth, WindowHeight;

    /// <summary>Camera angle to open the relief preview at, when given on the command line.</summary>
    public static double? StartupYaw, StartupPitch;

    private const string Help = """
        DepthView - depth map candidate inspector

          DepthView                       open the window
          DepthView <image>               open the window with that image loaded
          DepthView <image> --relief      also open the 3D relief preview
          DepthView <image> --tune-ui     also open the tuning dialog, optionally already set
                                          up: --blank <mm> --rim-mm <mm> --ramp-mm <mm>
                                          --passes <n>
          DepthView --about               open the About box: version, platforms, credits
          DepthView --licence             open the About box on its licence page
          DepthView <image> --screenshot <out.png> [--relief] [--delay <ms>]
                                          capture the window to a PNG and exit
                                          (used to keep the README images reproducible)
          DepthView --window <w> <h>      open the window at this size, to check the
                                          layout at screen sizes you do not own
          DepthView --report <path...>    write a text analysis instead of opening a window
          DepthView --report <dir>        analyse every image in a folder

        Options for --report
          --summary        one line per file instead of a full report
          --out <file>     write to this file (otherwise <image>-report.txt beside each input,
                           or depthview-report.txt for a folder or summary run)

        Headless relief render
          DepthView --render <image> [options]      write a lit relief render to a PNG
            --material <name>   material preset, matched loosely (default: polished brass)
            --albedo <image>    colour texture for the material
            --micro <image>     surface relief texture (light = high)
            --brushed           use generated brushed scratches as the surface relief
            --texscale <n>      texture repeats across the piece (default 1)
            --texrot <deg>      texture rotation
            --albstr <0..1>     colour texture strength
            --micstr <0..3>     surface relief strength
            --exag <n>          vertical exaggeration (default 1)
            --light <az> <el>   light bearing and elevation in degrees (default 315 42)
            --orbit <yaw> <el>  render in 3D from this camera bearing and elevation
                                (elevation 90 looks straight down; default 0 62)
            --zoom <n>          multiplies the fitted zoom; below 1 pulls back, which a
                                tilted view needs so the corners are not cropped
            --ao <n>            ambient occlusion strength (default 1)
            --slices <n>        quantise to n depth steps to preview terracing
            --size <px>         output width (default 900)
            --out <file>        output PNG (default <image>-relief.png)

        Tune a depth map (writes a new file, never over the original)
          DepthView --tune <image> [options]
            --out <file>        output PNG (default <image>-tuned.png)
            --black <level>     levels at or below this become pure black: one uniform
                                depth, which is how a noisy floor stops engraving mottled
            --white <level>     levels at or above this become pure white: no passes at all
            --no-stretch        keep the levels where they are instead of filling the range
            --rim <pct>         paint an untouched ring pct% of the radius wide, for the
                                raised rim on a coin blank, ramping into it so the
                                engraving rises to meet it instead of ending in a wall
            --ramp <pct>        ramp width, if it should differ from the rim width
            --mask <file>       also write the rim as its own image, for running the field
                                on separate laser settings
            --slices <n>        quantise to exactly n depths, matching a pass count
            --dither            scatter the slice boundaries, which breaks up the contour
                                rings a hard threshold leaves on smooth curves
            --invert            flip black/white, for art authored white-deepest
            --bits <8|16>       output bit depth (default 16)
            --passes <n>        pass count to report depths against (default 256)
          Measured in millimetres instead, which is how a rim is actually known:
            --blank <mm>        diameter of the blank, matched to the image's short side
            --rim-mm <mm>       rim width, measured inward from the edge
            --ramp-mm <mm>      ramp into the rim. Omitted means none: a hard step, which is
                                what a blank's own rim looks like. A ramp narrower than the
                                spot does nothing the beam was not going to do anyway
            --depth-mm <mm>     intended engraving depth. Changes no pixels; reports the
                                geometry the settings imply - microns per pass, and the wall
                                angle the ramp is asking the machine for
            --spot <um>         beam spot size, to check the map's resolution (default 7)
            --dpi <n>           override the resolution written into the PNG; with --blank
                                it is worked out for you, so the map imports at true size
          Black and white default to the 0.1 and 99.9 percentiles, because one stray pixel
          at an extreme is enough to make a min/max stretch do nothing.

        Calibration coupon (engrave it once per machine and material, then measure it)
          DepthView --calibrate [options]
            --blank <mm>        diameter of the blank the coupon is drawn for (default 40)
            --rim-mm <mm>       rim width to leave untouched at the edge (default 1.0)
            --size <px>         output width and height in pixels (default 4096)
            --steps <n>         steps in the depth wedge, 4 to 64 (default 16)
            --machine <name>    stamped into the file and the worksheet
            --material <name>   likewise, so a drawer of coupons stays identifiable
            --out <file>        output PNG (default depthview-calibration[-machine-material].png)
          Writes the coupon plus a worksheet to fill in at the bench. The coupon carries a
          depth wedge, a set of ramps at known wall angles, and a comb of shrinking gaps,
          so measuring one piece tells you the depth your settings actually reach, the
          steepest wall the machine will hold, and the finest detail its spot can resolve.
          The field is left uncut on purpose: the original surface is the datum you measure
          depths against.

        Exit codes: 0 all clean, 1 at least one file flagged as an imposter, 2 a file failed to load.
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help" or "/?" or "-?"))
        {
            AttachParentConsole();
            Console.WriteLine(Help);
            return 0;
        }

        int cidx = Array.FindIndex(args, a => a is "--calibrate");
        if (cidx >= 0) return RunCalibrate(args.Skip(cidx + 1).ToArray());

        int tidx = Array.FindIndex(args, a => a is "--tune");
        if (tidx >= 0) return RunTune(args.Skip(tidx + 1).ToArray());

        int ridx = Array.FindIndex(args, a => a is "--render");
        if (ridx >= 0) return RunRender(args.Skip(ridx + 1).ToArray());

        int idx = Array.FindIndex(args, a => a is "-r" or "--report");
        if (idx >= 0) return RunReport(args.Skip(idx + 1).ToArray());

        StartupFile = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        StartupRelief = args.Any(a => a is "--relief" or "-3d");
        StartupTune = args.Any(a => a is "--tune-ui");
        if (StartupTune)
        {
            StartupBlankMm = Flag(args, "--blank");
            StartupRimMm = Flag(args, "--rim-mm");
            StartupRampMm = Flag(args, "--ramp-mm");
            StartupPasses = Flag(args, "--passes") is double p && p >= 2 ? (int)p : null;
        }
        StartupAbout = args.Any(a => a is "--about");
        StartupLicence = args.Any(a => a is "--licence" or "--license");
        if (StartupLicence) StartupAbout = true;

        int sh = Array.FindIndex(args, a => a == "--screenshot");
        if (sh >= 0 && sh + 1 < args.Length) ScreenshotPath = args[sh + 1];

        int dl = Array.FindIndex(args, a => a == "--delay");
        if (dl >= 0 && dl + 1 < args.Length && int.TryParse(args[dl + 1], out int ms) && ms > 0)
            ScreenshotDelayMs = ms;

        int wn = Array.FindIndex(args, a => a == "--window");
        if (wn >= 0 && wn + 2 < args.Length
            && int.TryParse(args[wn + 1], out int ww) && ww > 200
            && int.TryParse(args[wn + 2], out int wh) && wh > 200)
        {
            WindowWidth = ww;
            WindowHeight = wh;
        }

        // --orbit <yaw> <elevation> opens the preview at a given camera angle, and implies it.
        int ob = Array.FindIndex(args, a => a == "--orbit");
        if (ob >= 0 && ob + 2 < args.Length
            && double.TryParse(args[ob + 1], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double oy)
            && double.TryParse(args[ob + 2], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double op))
        {
            StartupYaw = oy;
            StartupPitch = op;
            StartupRelief = true;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Value of a "--flag &lt;number&gt;" pair, or null when it is not there.</summary>
    private static double? Flag(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length
            && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : null;
    }

    // Referenced by the Avalonia previewer in Visual Studio and Rider.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // ------------------------------------------------------------------ headless mode

    private static int RunReport(string[] rest)
    {
        AttachParentConsole();

        bool summary = rest.Contains("--summary");
        string? outPath = null;
        var inputs = new List<string>();

        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--out" && i + 1 < rest.Length) { outPath = rest[++i]; continue; }
            if (rest[i].StartsWith('-')) continue;
            inputs.AddRange(Expand(rest[i]));
        }

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("No input files. Try: DepthView --report <image or folder>");
            return 2;
        }

        var sb = new StringBuilder();
        int exit = 0;

        foreach (var path in inputs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var (img, meta) = ImageLoader.Load(bytes, Path.GetFileName(path), path, "read from the command line");
                var result = DepthAnalyzer.Analyze(img, meta);

                if (result.VerdictSeverity == Severity.Alert) exit = Math.Max(exit, 1);

                if (summary)
                {
                    string line = ReportWriter.Summary(result);
                    sb.AppendLine(line);
                    Console.WriteLine(line);
                }
                else
                {
                    string text = ReportWriter.Build(result);
                    sb.AppendLine(text);
                    sb.AppendLine();
                    Console.WriteLine(text);

                    if (outPath is null && inputs.Count == 1)
                    {
                        string beside = Path.ChangeExtension(path, null) + "-report.txt";
                        File.WriteAllText(beside, text);
                        Console.WriteLine($"Written to {beside}");
                    }
                }
            }
            catch (Exception ex)
            {
                exit = 2;
                string line = $"ERROR {Path.GetFileName(path)}: {ex.Message}";
                sb.AppendLine(line);
                Console.Error.WriteLine(line);
            }
        }

        if (outPath is not null)
        {
            File.WriteAllText(outPath, sb.ToString());
            Console.WriteLine($"Written to {outPath}");
        }
        else if (summary || inputs.Count > 1)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(inputs[0])) ?? ".";
            string p = Path.Combine(dir, "depthview-report.txt");
            File.WriteAllText(p, sb.ToString());
            Console.WriteLine($"Written to {p}");
        }

        return exit;
    }

    // ------------------------------------------------------------------ calibration coupon

    /// <summary>
    /// Writes a calibration pattern to engrave, plus a text sheet to record the measurements on.
    ///
    /// Everything DepthView says about physical outcomes depends on the machine and the
    /// material, and no two are alike. Rather than hard-code one laser's numbers and quietly
    /// mislead everyone else, it emits a coupon: engrave it, measure it, and the figures stop
    /// being assumptions.
    /// </summary>
    private static int RunCalibrate(string[] rest)
    {
        AttachParentConsole();

        var spec = new CalibrationSpec();
        string? outPath = null;

        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            string? Next() => i + 1 < rest.Length ? rest[++i] : null;
            switch (a)
            {
                case "--out": outPath = Next(); break;
                case "--blank": if (double.TryParse(Next(), out double bd)) spec.BlankDiameterMm = bd; break;
                case "--rim-mm": if (double.TryParse(Next(), out double rm)) spec.RimMm = rm; break;
                case "--size": if (int.TryParse(Next(), out int sz)) spec.Pixels = sz; break;
                case "--steps": if (int.TryParse(Next(), out int st)) spec.WedgeSteps = Math.Clamp(st, 4, 64); break;
                case "--material": spec.Material = Next() ?? ""; break;
                case "--machine": spec.Machine = Next() ?? ""; break;
            }
        }

        try
        {
            var pat = CalibrationPattern.Build(spec);
            string tag = string.Join("-", new[] { spec.Machine, spec.Material }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Replace(' ', '-').ToLowerInvariant()));
            outPath ??= $"depthview-calibration{(tag.Length > 0 ? "-" + tag : "")}.png";

            PngEncoder.WriteGrey(outPath, pat.Pixels, pat.Width, pat.Height, 16, pat.Dpi, new[]
            {
                ("Software", $"DepthView {BuildInfo.Version}"),
                ("Comment", $"calibration coupon, {spec.BlankDiameterMm:F1} mm blank, " +
                            $"{spec.WedgeSteps} depth steps, machine={spec.Machine}, material={spec.Material}"),
            });

            string sheet = Path.ChangeExtension(outPath, null) + "-worksheet.txt";
            File.WriteAllText(sheet, Worksheet(spec, pat));

            Console.WriteLine($"Calibration coupon -> {outPath}");
            Console.WriteLine($"  blank           {spec.BlankDiameterMm:F1} mm, rim {spec.RimMm:F2} mm left untouched");
            Console.WriteLine($"  resolution      {pat.Width:N0} px, {pat.PixelsPerMm:F1} px/mm, "
                            + $"{pat.Dpi:F0} dpi, {1000 / pat.PixelsPerMm:F1} um/pixel");
            foreach (string line in pat.Legend) Console.WriteLine("  " + line);
            Console.WriteLine($"  worksheet       {Path.GetFileName(sheet)}");
            Console.WriteLine();
            Console.WriteLine("  Black is deepest, white is untouched. The field is left uncut, so the");
            Console.WriteLine("  original surface is your datum to measure depths against.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Calibration failed: " + ex.Message);
            return 2;
        }
    }

    private static string Worksheet(CalibrationSpec spec, CalibrationPattern.Result pat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DepthView calibration worksheet");
        sb.AppendLine("===============================");
        sb.AppendLine();
        sb.AppendLine($"Machine   : {(spec.Machine.Length > 0 ? spec.Machine : "____________________")}");
        sb.AppendLine($"Material  : {(spec.Material.Length > 0 ? spec.Material : "____________________")}");
        sb.AppendLine("Power     : ____________  Speed: ____________  Frequency: ____________");
        sb.AppendLine("Passes    : ____________  Date : ____________");
        sb.AppendLine();
        sb.AppendLine($"Blank {spec.BlankDiameterMm:F1} mm, {pat.Width} px, {pat.PixelsPerMm:F1} px/mm, "
                    + $"{1000 / pat.PixelsPerMm:F1} um/pixel");
        sb.AppendLine();
        sb.AppendLine("1. DEPTH  - measure each step against the unengraved field, in microns.");
        sb.AppendLine("   Step 1 is fully deep (black); the last step is untouched (white).");
        sb.AppendLine("   This is the one that matters most: metal does not ablate linearly as the");
        sb.AppendLine("   pocket deepens, so a linear depth map does not give linear depth.");
        sb.AppendLine();
        for (int i = 1; i <= spec.WedgeSteps; i++)
        {
            double commanded = 1.0 - (i - 1) / (double)(spec.WedgeSteps - 1);
            sb.AppendLine($"   step {i,2}   commanded {commanded * 100,5:F1}% of full depth   measured ______ um");
        }
        sb.AppendLine();
        sb.AppendLine("2. RAMP   - which ramp widths came out as a clean shoulder?");
        sb.AppendLine("   The narrowest clean one is the minimum usable ramp on this material.");
        sb.AppendLine();
        foreach (double r in spec.RampsMm)
            sb.AppendLine($"   {(r <= 0 ? "hard step" : r.ToString("0.00") + " mm"),-12}  clean / rough / not usable   (circle one)");
        sb.AppendLine();
        sb.AppendLine("3. SPOT   - the finest pitch still resolved as separate lines.");
        sb.AppendLine("   That is the effective spot on this material, which is often not the");
        sb.AppendLine("   figure on the spec sheet.");
        sb.AppendLine();
        foreach (double p in spec.CombPitchUm)
            sb.AppendLine($"   {p,4:F0} um    resolved / merged   (circle one)");
        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine("  ____________________________________________________________");
        sb.AppendLine("  ____________________________________________________________");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ headless tuning

    /// <summary>
    /// Writes a tuned copy of a depth map without opening a window, so a whole folder can be
    /// put through the same treatment, and so every part of the tuning path is exercisable
    /// from a script and from CI rather than only by hand.
    ///
    /// Never writes over the input. A tuned map is a new file, always.
    /// </summary>
    private static int RunTune(string[] rest)
    {
        AttachParentConsole();

        string? input = null, outPath = null, maskPath = null;
        var o = new TuningOptions();
        bool haveBlack = false, haveWhite = false;
        double? rimPct = null, rampPct = null;
        int passes = 256;
        // WeCreat support give 6-8 um for the Lumos Ultra UV spot; 7 sits in the middle.
        double spotMicrons = 7;

        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            string? Next() => i + 1 < rest.Length ? rest[++i] : null;

            switch (a)
            {
                case "--out": outPath = Next(); break;
                case "--mask": maskPath = Next(); break;
                case "--black": if (int.TryParse(Next(), out int b)) { o.BlackPoint = b; haveBlack = true; } break;
                case "--white": if (int.TryParse(Next(), out int w)) { o.WhitePoint = w; haveWhite = true; } break;
                case "--no-stretch": o.Stretch = false; break;
                case "--rim": if (double.TryParse(Next(), out double rp)) { rimPct = rp; o.AddRim = true; } break;
                case "--ramp": if (double.TryParse(Next(), out double rr)) rampPct = rr; break;
                case "--slices": if (int.TryParse(Next(), out int s)) o.Slices = s; break;
                case "--dither": o.Dither = true; break;
                case "--invert": o.Invert = true; break;
                case "--passes": if (int.TryParse(Next(), out int pp) && pp > 1) passes = pp; break;
                case "--dpi": if (double.TryParse(Next(), out double d)) o.Dpi = d; break;
                case "--blank": if (double.TryParse(Next(), out double bd2)) o.BlankDiameterMm = bd2; break;
                case "--rim-mm": if (double.TryParse(Next(), out double rmm)) o.RimWidthMm = rmm; break;
                case "--ramp-mm": if (double.TryParse(Next(), out double ramm)) o.RimRampMm = ramm; break;
                case "--spot": if (double.TryParse(Next(), out double sp)) spotMicrons = sp; break;
                case "--depth-mm": if (double.TryParse(Next(), out double dm)) o.TargetDepthMm = dm; break;
                case "--bits": if (int.TryParse(Next(), out int bd)) o.OutputBitDepth = bd; break;
                default:
                    if (!a.StartsWith('-') && input is null) input = a;
                    break;
            }
        }

        if (input is null || !File.Exists(input))
        {
            Console.Error.WriteLine("Usage: DepthView --tune <image> [options]. See --help.");
            return 2;
        }

        try
        {
            var loaded = ImageLoader.Load(File.ReadAllBytes(input), Path.GetFileName(input), input, "tuning");
            var before = DepthAnalyzer.Analyze(loaded.Image, loaded.Meta);
            var grey = DepthTuner.ExtractGrey(loaded.Image);
            int maxValue = loaded.Image.MaxValue;

            // Unstated level points come from percentiles, not min/max: a handful of stray
            // pixels at either extreme is common, and one of them makes a min/max stretch
            // do nothing at all.
            var (sb, sw) = DepthTuner.SuggestLevels(before.GreyHistogram);
            if (!haveBlack) o.BlackPoint = sb;
            if (!haveWhite) o.WhitePoint = sw;

            // Millimetres win over percentages when both are given: one came off a pair of
            // calipers and the other is a guess.
            o.ResolvePhysical(loaded.Image.Width, loaded.Image.Height);
            if (rimPct is double pct && o.RimWidthMm is null)
            {
                double half = Math.Min(loaded.Image.Width, loaded.Image.Height) / 2.0;
                o.RimRadius = half * (1 - pct / 100.0);
                o.RimRamp = half * ((rampPct ?? pct) / 100.0);
            }

            var tuned = DepthTuner.Apply(grey, loaded.Image.Width, loaded.Image.Height,
                                         maxValue, o, out var rep);

            outPath ??= Path.ChangeExtension(input, null) + "-tuned.png";

            // Shared with the Tune window, deliberately: a file written from the dialog and one
            // written here with the same settings are the same bytes, which is only true while
            // there is one implementation of "write it out".
            TuneJob.WriteTuned(outPath, tuned, loaded.Image.Width, loaded.Image.Height,
                               maxValue, o, Path.GetFileName(input));

            if (maskPath is not null && o.AddRim)
            {
                TuneJob.WriteRimMask(maskPath, loaded.Image.Width, loaded.Image.Height, o);
                Console.WriteLine($"  mask            {Path.GetFileName(maskPath)} (white = engraved area)");
            }

            // Re-analyse what was written. The tool marking its own homework is the point:
            // the claim that tuning helped should be a measurement, not an assertion.
            var reloaded = ImageLoader.Load(File.ReadAllBytes(outPath), Path.GetFileName(outPath), outPath, "tuned");
            var after = DepthAnalyzer.Analyze(reloaded.Image, reloaded.Meta);

            var (dBefore, wBefore) = before.SlicesAt(passes);
            var (dAfter, wAfter) = after.SlicesAt(passes);

            Console.WriteLine($"Tuned {Path.GetFileName(input)} -> {Path.GetFileName(outPath)}");
            if (o.PixelsPerMm(loaded.Image.Width, loaded.Image.Height) is double ppmm)
            {
                var check = ResolutionCheck.For(1000.0 / ppmm, spotMicrons);
                Console.WriteLine($"  physical        {o.BlankDiameterMm:F1} mm across {Math.Min(loaded.Image.Width, loaded.Image.Height):N0} px"
                                + $"  =  {ppmm:F1} px/mm, {o.Dpi:F0} dpi");
                Console.WriteLine($"  resolution      {check.MicronsPerPixel:F1} um/pixel against a {spotMicrons:F0} um spot"
                                + $"  -  {check.Note}");
                if (o.RimWidthMm is double rw)
                {
                    double rampMm = o.RimRampMm ?? 0;
                    Console.WriteLine($"  rim             {rw:F2} mm = {rw * ppmm:F0} px"
                                    + (rampMm <= 0
                                        ? ", hard step at the rim (no ramp)"
                                        : $", ramp {rampMm:F2} mm = {rampMm * ppmm:F0} px"));

                    // A ramp narrower than the beam is the worst of both: the spot smears the
                    // transition to its own width regardless, so the ramp achieves nothing the
                    // optics were not going to do anyway.
                    if (rampMm > 0 && rampMm * 1000 < spotMicrons)
                        Console.WriteLine($"                  note: that ramp is {rampMm * 1000:F1} um, narrower than the"
                                        + $" {spotMicrons:F0} um spot. The beam will smear the edge to about its own"
                                        + " width either way, so this is doing nothing a hard step would not.");

                    if (o.TargetDepthMm is double dep && dep > 0)
                    {
                        if (rampMm > 0)
                        {
                            double angle = Math.Atan2(dep, rampMm) * 180 / Math.PI;
                            Console.WriteLine($"  wall            {dep:F2} mm deep over a {rampMm:F2} mm ramp"
                                            + $"  =  {angle:F0} deg from horizontal");
                            if (angle > 75)
                                Console.WriteLine("                  that is very steep. Whether the machine holds it is a"
                                                + " question for a test piece: ablated pockets taper as they deepen.");
                        }
                        else
                        {
                            Console.WriteLine($"  wall            {dep:F2} mm deep with a hard step - the wall angle will be"
                                            + " whatever the optics and the taper give you, not what the map asked for.");
                        }
                        Console.WriteLine($"  depth per pass  {dep * 1000 / passes:F1} um at {passes:N0} passes"
                                        + $"  ({dep:F2} mm total)");
                    }
                }
            }
            Console.WriteLine($"  levels          black {o.BlackPoint:N0}, white {o.WhitePoint:N0}"
                            + (o.Stretch ? ", stretched" : ", not stretched"));
            Console.WriteLine($"  flattened       {rep.FlattenedToBlack:N0} px to pure black, "
                            + $"{rep.LiftedToWhite:N0} px to pure white");
            if (o.AddRim) Console.WriteLine($"  rim             {rep.Summary}");
            Console.WriteLine($"  changed         {rep.Changed:N0} of {grey.Length:N0} pixels");
            Console.WriteLine($"  depths @ {passes,-4}   {dBefore:N0} -> {dAfter:N0}"
                            + $"   (wasted passes {wBefore:N0} -> {wAfter:N0})");
            Console.WriteLine($"  range use       {before.RangeUtilisation * 100:F1}% -> {after.RangeUtilisation * 100:F1}%");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Tune failed: " + ex.Message);
            return 2;
        }
    }

    // ------------------------------------------------------------------ headless relief render

    /// <summary>
    /// Renders the relief preview without opening a window, so previews can be scripted
    /// across a folder of candidates or a sweep of materials and light angles.
    /// </summary>
    private static int RunRender(string[] rest)
    {
        AttachParentConsole();

        string? input = null, outPath = null, materialName = "polished brass";
        string? albedo = null, micro = null, generated = null;
        bool brushed = false, orbit = false;
        double exag = 1, az = 315, el = 42, ao = 1, yaw = 0, pitch = 62, zoomMul = 1;
        double texScale = double.NaN, texRot = double.NaN, albStr = double.NaN, micStr = double.NaN;
        int slices = 0, size = 900;

        double D(string[] a, ref int i, double fallback)
            => i + 1 < a.Length && double.TryParse(a[i + 1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? (i++, v).v : fallback;

        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--out": if (i + 1 < rest.Length) outPath = rest[++i]; break;
                case "--material": if (i + 1 < rest.Length) materialName = rest[++i]; break;
                case "--albedo": if (i + 1 < rest.Length) albedo = rest[++i]; break;
                case "--micro": if (i + 1 < rest.Length) micro = rest[++i]; break;
                case "--brushed": brushed = true; break;
                case "--generated": if (i + 1 < rest.Length) generated = rest[++i]; break;
                case "--texscale": texScale = D(rest, ref i, 1); break;
                case "--texrot": texRot = D(rest, ref i, 0); break;
                case "--albstr": albStr = D(rest, ref i, 1); break;
                case "--micstr": micStr = D(rest, ref i, 1); break;
                case "--exag": exag = D(rest, ref i, 1); break;
                case "--ao": ao = D(rest, ref i, 1); break;
                case "--slices": slices = (int)D(rest, ref i, 0); break;
                case "--size": size = (int)D(rest, ref i, 900); break;
                case "--light": az = D(rest, ref i, 315); el = D(rest, ref i, 42); break;
                case "--orbit": orbit = true; yaw = D(rest, ref i, 0); pitch = D(rest, ref i, 62); break;
                case "--zoom": zoomMul = D(rest, ref i, 1); break;
                default:
                    if (!rest[i].StartsWith('-') && input is null) input = rest[i];
                    break;
            }
        }

        if (input is null || !File.Exists(input))
        {
            Console.Error.WriteLine("Usage: DepthView --render <image> [options].  See --help.");
            return 2;
        }

        try
        {
            var bytes = File.ReadAllBytes(input);
            var (img, _) = ImageLoader.Load(bytes, Path.GetFileName(input), input, "headless render");
            var field = Rendering.ReliefRenderer.BuildHeights(img, 1400, out int fw, out int fh);
            var scene = new Rendering.ReliefScene(field, fw, fh);

            var presets = Rendering.MaterialLibrary.Presets;
            var m = presets.FirstOrDefault(p =>
                        p.Name.Contains(materialName, StringComparison.OrdinalIgnoreCase))
                    ?? presets[0];

            if (albedo is not null) m.AlbedoTexturePath = albedo;
            if (micro is not null) m.MicroTexturePath = micro;
            if (brushed) { m.ProceduralTexture = "brushed"; m.MicroTexturePath = null; }
            if (generated is not null) m.ProceduralTexture = generated.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : generated;
            if (!double.IsNaN(texScale)) m.TextureScale = texScale;
            if (!double.IsNaN(texRot)) m.TextureRotationDeg = texRot;
            if (!double.IsNaN(albStr)) m.AlbedoStrength = albStr;
            if (!double.IsNaN(micStr)) m.MicroStrength = micStr;
            m.InvalidateTextures();

            int w = Math.Clamp(size, 64, 4000);
            int h = Math.Max(64, (int)Math.Round(w * (double)fh / fw));

            var o = new Rendering.ReliefOptions
            {
                Material = m,
                LightAzimuthDeg = az,
                LightElevationDeg = el,
                AoStrength = ao,
                Exaggeration = exag,
                SliceCount = slices,
                Zoom = Math.Min((double)w / fw, (double)h / fh) * Math.Clamp(zoomMul, 0.05, 20),
                Quality = 1,
                Orbit = orbit,
                YawDeg = yaw,
                PitchDeg = pitch,
                MeshResolution = 720,
                Supersample = 2
            };

            var buf = new byte[(long)w * h * 4];
            var sw = Stopwatch.StartNew();
            Rendering.ReliefRenderer.Render(buf, w, h, scene, o);
            sw.Stop();

            outPath ??= Path.ChangeExtension(input, null) + "-relief.png";

            using (var outImg = SixLabors.ImageSharp.Image.LoadPixelData<
                       SixLabors.ImageSharp.PixelFormats.Bgra32>(buf, w, h))
            {
                outImg.SaveAsPng(outPath);
            }

            if (m.TextureError is { } te) Console.Error.WriteLine(te);

            Console.WriteLine($"{m.Name}: {w}x{h} in {sw.ElapsedMilliseconds} ms -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Render failed: " + ex.Message);
            return 2;
        }
    }

    private static IEnumerable<string> Expand(string input)
    {
        if (Directory.Exists(input))
        {
            var exts = ImageLoader.SupportedPatterns.Split(';')
                .Select(p => p.TrimStart('*'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Directory.EnumerateFiles(input)
                .Where(f => exts.Contains(Path.GetExtension(f)));
        }

        if (input.Contains('*') || input.Contains('?'))
        {
            string dir = Path.GetDirectoryName(input) ?? "";
            if (string.IsNullOrEmpty(dir)) dir = ".";
            return Directory.EnumerateFiles(dir, Path.GetFileName(input));
        }

        return new[] { input };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// The app is built as a windowed executable so launching it never flashes a console.
    /// In report mode we borrow the calling shell's console so output is visible there too.
    /// </summary>
    private static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AttachConsole(-1); } catch { /* no console to attach to; --out still works */ }
    }
}
