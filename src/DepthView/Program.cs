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

internal static partial class Program
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

    /// <summary>
    /// Target depth and thickness (mm) and exaggeration (stops, 0 = true scale) to open either
    /// relief view with, from --depth-mm, --thick and --exag. Same meaning as in --render. The
    /// three blank figures go straight into the shared <see cref="Blank"/> for this run only.
    /// </summary>
    public static double? StartupDepthMm, StartupThickMm, StartupExagStops;

    /// <summary>Fit policy to open the tuning dialog with, from the same --fit flag as --tune.</summary>
    public static FitPolicy StartupFit = FitPolicy.None;

    /// <summary>More of the tuning dialog's settings from the command line, as --tune takes them.</summary>
    public static int? StartupBlack, StartupWhite, StartupBits;
    public static double? StartupSpot;
    public static bool StartupCoverRim, StartupOutline, StartupWriteDpi, StartupUniformSurround;

    /// <summary>Open the tuning wizard over the Tune window; optionally at a step (1-based) and for a target.</summary>
    public static bool StartupWizard;
    public static int? StartupWizardStep;
    public static WizardTarget? StartupWizardTarget;

    /// <summary>Open the About box straight away. Exists so its screenshot is reproducible too.</summary>
    public static bool StartupAbout;
    public static bool StartupWhatsNew;

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
                                          --passes <n> --black <level> --white <level>
                                          --bits <8|16> --spot <um> --fit [mode] --cover-rim
                                          --outline --write-dpi --uniform-surround (tick
                                          those boxes)
                                          Either relief view also takes --blank <mm>,
                                          --thick <mm>, --depth-mm <mm> and --exag <stops>,
                                          as in --render
          DepthView <image> --wizard      open the tuning wizard over the Tune window;
                                          --wizard-step <n> opens it at step n, and
                                          --wizard-target <makeit|lightburn|slicer> picks
                                          the first answer (for screenshots)
          DepthView --about               open the About box: version, platforms, credits
          DepthView --licence             open the About box on its licence page
          DepthView <image> --screenshot <out.png> [--relief] [--delay <ms>]
                                          capture the window to a PNG and exit
                                          (used to keep the README images reproducible)
          DepthView --window <w> <h>      open the window at this size, to check the
                                          layout at screen sizes you do not own
          DepthView --version             print the version and exit
          DepthView --whats-new           print this version's release notes (built in)
          DepthView --whats-new-ui        open them in a window
          DepthView --check-update        ask GitHub whether a newer release exists
          DepthView --update              download, verify and install it in place (a copy
                                          unpacked from a release zip only). Nothing is
                                          replaced unless every check passes
          DepthView --report <path...>    write a text analysis instead of opening a window
          DepthView --report <dir>        analyse every image in a folder

        Options for --report
          --summary        one line per file instead of a full report
          --out <file>     write to this file (otherwise <image>-report.txt beside each input,
                           or depthview-report.txt for a folder or summary run)
          --json           one JSON document instead of text, for another program to read
                           (schema depthview.report/1, see docs/INTEGRATION.md). Only JSON goes
                           to stdout, and nothing is written beside the input unless --out is given
          --passes <n,...> pass counts for the JSON table (default 64,100,128,200,256,512,1024)
          --histogram      with --json, also list every occupied level and its pixel count

        G-code: what a job file actually sends the machine
          DepthView --gcode <file.gc> [--json] [--out <file>]
                           distinct power levels, scan-line spacing, the spacing of power
                           changes along a line, cutting heights, and each group of settings
                           in MakeIt's units. Reads MakeIt's staged job (Ctrl+Shift+P in
                           MakeIt opens its folder) and LightBurn G-code; gzipped files too.
                           --json gives schema depthview.gcode/1, see docs/INTEGRATION.md

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
            --blank <mm>        blank diameter the short side spans
            --thick <mm>        blank thickness; the 3D view stands on a slab this thick
            --depth-mm <mm>     target depth of the deepest cut
                                These three default to the blank last saved in the
                                window (40 mm, 4 mm, 18% of thickness until changed).
                                Target depth follows thickness unless given
            --exag <stops>      vertical exaggeration in doublings around the target
                                depth: 0 (the default) is true scale, Z in the same
                                mm as X and Y; 3 draws it 8x deep to hunt terracing;
                                -8 is flat. Up to 1.3.0 this was a raw ratio where 1
                                drew about 5 mm on a 40 mm blank
            --light <az> <el>   light bearing and elevation in degrees (default 315 42)
            --orbit <yaw> <el>  render in 3D from this camera bearing and elevation
                                (elevation 90 looks straight down; default 0 62)
            --zoom <n>          multiplies the fitted zoom; below 1 pulls back, which a
                                tilted view needs so the corners are not cropped
            --ao <n>            ambient occlusion strength (default 1)
            --slices <n>        quantise to n depth steps to preview terracing
            --size <px>         output width (default 900)
            --out <file>        output PNG (default <image>-relief.png)

        What the tuning wizard measures, without changing anything
          DepthView --survey <image> [--passes <n>] [--json]
                              background or floor, placement, the artwork's own rim, the
                              floor and top, pixel noise, and the nearly level areas with how
                              many slice boundaries each crosses at the pass count

        Tune a depth map (writes a new file, never over the original)
          DepthView --tune <image> [options]
            --out <file>        output PNG (default <image>-tuned.png)
            --black <level>     levels at or below this become pure black: one uniform
                                depth, which is how a noisy floor stops engraving mottled
            --white <level>     levels at or above this become pure white: no passes at all
            --no-stretch        keep the levels where they are instead of filling the range
            --fit [content|canvas|design]
                                grow the canvas so the design clears the rim, instead of
                                letting the rim paint over whatever runs past it. Nothing is
                                resampled: the original pixels are copied into the middle of
                                a larger square, so the same artwork simply spans fewer mm of
                                the same blank. "content" (the default) grows until the
                                furthest engraved pixel clears the rim; "canvas" grows until
                                all four corners do, which cannot clip anything but costs the
                                diagonal - a square inside a circle gives up a factor of root
                                two, so a 40 mm blank carries about 27 mm of art. "design"
                                centres the blank on the design itself rather than on the
                                canvas, and sizes it so the design fills the blank inside the
                                rim - for a coin drawn off-centre, or on a wide surround of
                                background. It may crop, but only background: a crop that
                                would remove one pixel of design is refused
            --levels-from <design|floor>
                                level points as the tuning wizard suggests them: from the
                                design itself, leaving out a surround ("design"), or with the
                                background counted as a cut-away floor ("floor"). A floor or a
                                flat top that holds a real share of the design becomes one
                                exact level
            --flat <leave|smooth|flatten,...>
                                one word per nearly level area, largest first, as --survey
                                lists them: smooth removes pixel jitter only, flatten makes the
                                area one level (and changes the design to do it)
            --cover-rim         with --fit design: if the artwork has a raised rim of its own,
                                size the blank so that rim lands under the new one, which
                                replaces it. What was found is reported; if no rim is found,
                                nothing is covered
            --uniform-surround  for art on a shaded or vignetted backdrop: everything joined to
                                the image's edge at the edge's own levels becomes one level
                                before anything else, so the shading is not taken for design
                                reaching the corners (which would shrink the coin to fit
                                them). --survey says when a surround is shaded
            --pad <background|untouched>
                                what the new ring between the artwork and the rim is cut to.
                                "background" (the default) carries the design's own field out
                                to the rim, so there is no step where the original file ended,
                                but on art with a cut-away floor that means engraving the whole
                                ring to full depth. "untouched" cuts nothing there, which only
                                looks right when the design's background is already near
                                untouched
            --rim <pct>         paint an untouched ring pct% of the radius wide, for the
                                raised rim on a coin blank, ramping into it so the
                                engraving rises to meet it instead of ending in a wall
            --ramp <pct>        ramp width, if it should differ from the rim width
            --mask <file>       also write the rim as its own image, for running the field
                                on separate laser settings
            --outline [file]    also write a vector outline (SVG, true size) to import
                                alongside the map: circles at the blank's edge and at the
                                edge of the engraved area, plus a centre mark. A depth map
                                cannot help you align a round design to a round blank,
                                because an image frames as its own rectangle whatever is
                                drawn in it. Put these on a tool layer, turn framing off for
                                the image layer, and frame with Hull or Contour
            --slices <n>        quantise to exactly n depths, matching a pass count
            --dither            scatter the slice boundaries, which breaks up the contour
                                rings a hard threshold leaves on smooth curves
            --invert            flip black/white, for art authored white-deepest
            --bits <8|16>       output bit depth (default 16)
            --passes <n>        pass count to report depths against (default 256)
            --json              report what was done as one JSON document on stdout
                                (schema depthview.tune/1, see docs/INTEGRATION.md)
          --out may not name the input file: the original is never written over.
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
            --no-dpi            write no resolution at all, as the Tune window does when its
                                "write DPI" box is clear
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
          depths against. Measure it with a depth gauge or microscope, not a scale.

        Mass-loss coupon (for a milligram scale: one setting per coupon, weigh each one)
          DepthView --calibrate --mass [options]
            --coupon <mm>       edge of the square coupon (default 25)
            --zone <mm>         edge of the square zone engraved at one setting (default 15)
            --thick <mm>        stock thickness, to check coupon mass against the scale (default 3)
            --scale-g <g>       scale capacity in grams (default 50)
            --rows <n>          coupons in the series, one worksheet row each (default 10)
            --material <name>   brass, C360, copper, stainless/304, aluminium/6061/1050;
                                sets the density used for depth and sensitivity
            --machine <name>    stamped into the file and the worksheet
            --out <file>        output PNG (default depthview-mass-coupon[-machine-material].png)
          Writes one image to engrave on every coupon in a series, plus a worksheet with a
          row per coupon for its settings and before/after weights. Each coupon's mass loss
          gives the removal efficiency the depth model runs on. No labels are engraved:
          anything engraved counts as removed mass.

        Exit codes: 0 all clean, 1 at least one file flagged (an imposter, or a picture that is mostly
        colour rather than a depth map), 2 a file failed to load.
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

        if (args.Any(a => a is "--version" or "-V"))
        {
            // One line, last token the bare version: the updater runs a freshly downloaded copy
            // with this flag and refuses to install it unless the number is the one it expected.
            AttachParentConsole();
            Console.WriteLine($"DepthView {BuildInfo.Version}");
            return 0;
        }

        // Release notes, as text. Exit 1 when the notes built in are not this version's - CI
        // runs this, so a release cannot be built with last release's notes inside it.
        if (args.Any(a => a is "--whats-new"))
        {
            AttachParentConsole();
            var notes = Updates.ReleaseNotes.ForThisBuild();
            if (notes is null)
            {
                Console.Error.WriteLine($"No release notes for {BuildInfo.Version} are built in (they describe "
                    + $"{Updates.ReleaseNotes.NotesVersion(Updates.ReleaseNotes.Markdown) ?? "nothing"}). "
                    + "Update .github/RELEASE_TEMPLATE.md.");
                return 1;
            }
            Console.Write(Updates.ReleaseNotes.PlainText(notes));
            Console.WriteLine();
            Console.WriteLine("Full release page: " + Updates.ReleaseNotes.PageUrl);
            return 0;
        }

        int fidx = Array.IndexOf(args, "--update-feed");
        if (fidx >= 0 && fidx + 1 < args.Length) Updates.UpdateService.FeedOverride = args[fidx + 1];

        if (args.Any(a => a is "--check-update")) return RunCheckUpdate(install: false);
        if (args.Any(a => a is "--update")) return RunCheckUpdate(install: true);

        int cidx = Array.FindIndex(args, a => a is "--calibrate");
        if (cidx >= 0) return RunCalibrate(args.Skip(cidx + 1).ToArray());

        int tidx = Array.FindIndex(args, a => a is "--tune");
        if (tidx >= 0) return RunTune(args.Skip(tidx + 1).ToArray());

        int ridx = Array.FindIndex(args, a => a is "--render");
        if (ridx >= 0) return RunRender(args.Skip(ridx + 1).ToArray());

        int svidx = Array.FindIndex(args, a => a is "--survey");
        if (svidx >= 0) return RunSurvey(args.Skip(svidx + 1).ToArray());

        int gidx = Array.FindIndex(args, a => a is "--gcode");
        if (gidx >= 0) return RunGcode(args.Skip(gidx + 1).ToArray());

        int pidx = Array.FindIndex(args, a => a is "--project");
        if (pidx >= 0) return RunProject(args.Skip(pidx + 1).ToArray());

        int lbidx = Array.FindIndex(args, a => a is "--lb");
        if (lbidx >= 0) return RunLightBurnControl(args.Skip(lbidx + 1).ToArray());

        int idx = Array.FindIndex(args, a => a is "-r" or "--report");
        if (idx >= 0) return RunReport(args.Skip(idx + 1).ToArray());

        StartupFile = args.Where((a, i) => i == 0 || args[i - 1] != "--update-feed")
                          .FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        StartupRelief = args.Any(a => a is "--relief" or "-3d");
        StartupTune = args.Any(a => a is "--tune-ui");

        // --wizard opens the tuning wizard over the Tune window, at a step and for a target if
        // asked - so every step's layout can be captured from a script, not just the first.
        StartupWizard = args.Any(a => a is "--wizard");
        if (StartupWizard) StartupTune = true;
        StartupWizardStep = Flag(args, "--wizard-step") is double wsStep ? (int)wsStep : null;
        int wti = Array.IndexOf(args, "--wizard-target");
        if (wti >= 0 && wti + 1 < args.Length)
            StartupWizardTarget = args[wti + 1].ToLowerInvariant() switch
            {
                "lightburn" or "lb" => WizardTarget.LightBurn,
                "slicer" or "slice" or "galvo" => WizardTarget.Slicer,
                _ => WizardTarget.MakeIt,
            };
        // Read whatever opens a relief view: --relief, --tune-ui, or --orbit further down.
        StartupBlankMm = Flag(args, "--blank");
        StartupThickMm = Flag(args, "--thick");
        StartupDepthMm = Flag(args, "--depth-mm");
        StartupExagStops = Flag(args, "--exag");
        Blank.Current.ApplyTransient(StartupBlankMm, StartupThickMm, StartupDepthMm);

        // An update renames the old program aside because a running file cannot be replaced on
        // Windows. This start is the first moment it can be deleted; a few seconds later, so the
        // process it belonged to has certainly gone.
        _ = System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ => Updates.UpdateService.CleanupLeftovers(),
            System.Threading.Tasks.TaskScheduler.Default);
        if (StartupTune)
        {
            StartupRimMm = Flag(args, "--rim-mm");
            StartupRampMm = Flag(args, "--ramp-mm");
            StartupPasses = Flag(args, "--passes") is double p && p >= 2 ? (int)p : null;

            // The rest of the dialog's settings, so a fully tuned state can be opened - and
            // captured - from a script. Each means exactly what it means to --tune.
            StartupBlack = Flag(args, "--black") is double blackLevel ? (int)blackLevel : null;
            StartupWhite = Flag(args, "--white") is double whiteLevel ? (int)whiteLevel : null;
            StartupBits = Flag(args, "--bits") is double outBits ? (int)outBits : null;
            StartupSpot = Flag(args, "--spot");
            StartupCoverRim = args.Any(a => a is "--cover-rim");
            if (StartupCoverRim) StartupFit = FitPolicy.Design;
            StartupOutline = args.Any(a => a is "--outline");
            StartupWriteDpi = args.Any(a => a is "--write-dpi");
            StartupUniformSurround = args.Any(a => a is "--uniform-surround");

            int fi = Array.IndexOf(args, "--fit");
            if (fi >= 0)
            {
                string mode = fi + 1 < args.Length ? args[fi + 1].ToLowerInvariant() : "";
                StartupFit = mode switch
                {
                    "canvas" => FitPolicy.Canvas,
                    "design" => FitPolicy.Design,
                    _ => FitPolicy.Content,
                };
            }
        }
        StartupAbout = args.Any(a => a is "--about");
        StartupWhatsNew = args.Any(a => a is "--whats-new-ui");
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

        // Before any window can save a preference: is this the first start of a newer version?
        Updates.ReleaseNotes.Probe();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// --check-update and --update: the same check and install the window offers, from a
    /// terminal. --update installs but does not restart anything - whoever ran it is in charge
    /// of what runs next. Exit codes: 0 up to date or installed, 1 newer but not installable
    /// here, 2 the check or the install failed.
    /// </summary>
    private static int RunCheckUpdate(bool install)
    {
        AttachParentConsole();
        var info = Updates.UpdateService.CheckAsync(force: true).GetAwaiter().GetResult();
        if (info.Error is not null) { Console.Error.WriteLine(info.Error); return 2; }

        Console.WriteLine($"This copy:  DepthView {info.Current} ({Updates.UpdateService.CurrentRid() ?? "unknown platform"})");
        Console.WriteLine($"Latest:     DepthView {info.Latest}   {info.NotesUrl}");
        if (!info.Newer) { Console.WriteLine("Up to date."); return 0; }
        if (!info.CanInstall) { Console.WriteLine("Cannot update in place: " + info.InstallBlocker); return 1; }
        if (!install) { Console.WriteLine("A newer version is available. Run with --update to install it."); return 0; }

        var layout = Updates.UpdateService.CurrentLayout()!;
        string last = "";
        var progress = new Progress<(string Stage, double? Fraction)>(p =>
        {
            if (p.Stage != last) { Console.WriteLine(p.Stage + " ..."); last = p.Stage; }
        });
        try
        {
            var changed = Updates.UpdateService.InstallAsync(info, layout, progress).GetAwaiter().GetResult();
            Console.WriteLine($"Installed DepthView {info.Latest}: {string.Join(", ", changed)}");
            Console.WriteLine($"What's new: run DepthView --whats-new, or see {info.NotesUrl}");
            return 0;
        }
        catch (Updates.UpdateException ex)
        {
            Console.Error.WriteLine("Not installed: " + ex.Message + " Nothing was changed.");
            return 2;
        }
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
        bool json = rest.Contains("--json");
        bool histogram = rest.Contains("--histogram");
        string? outPath = null;
        int[]? passCounts = null;
        var inputs = new List<string>();

        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--out" && i + 1 < rest.Length) { outPath = rest[++i]; continue; }
            if (rest[i] == "--passes" && i + 1 < rest.Length) { passCounts = ParsePassList(rest[++i]); continue; }
            if (rest[i].StartsWith('-')) continue;
            inputs.AddRange(Expand(rest[i]));
        }

        if (inputs.Count == 0)
        {
            if (json) Console.WriteLine(JsonReport.Error(JsonReport.ReportSchema, "No input files."));
            Console.Error.WriteLine("No input files. Try: DepthView --report <image or folder>");
            return 2;
        }

        if (json) return RunReportJson(inputs, outPath, passCounts ?? JsonReport.DefaultPassCounts, histogram);

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

    /// <summary>
    /// --report --json: the same analysis, as one JSON document for another program to read.
    /// docs/INTEGRATION.md is the specification.
    ///
    /// Only JSON goes to stdout - no progress lines, no "Written to" - so a caller can parse
    /// it whole. Nothing is written beside the input unless --out asks for it. Exit codes are
    /// the text report's: 0 clean, 1 an imposter found, 2 a file could not be read.
    /// </summary>
    private static int RunReportJson(List<string> inputs, string? outPath, IReadOnlyList<int> passCounts, bool histogram)
    {
        var entries = new List<JsonReport.Entry>();
        int exit = 0;

        foreach (var path in inputs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var (img, meta) = ImageLoader.Load(File.ReadAllBytes(path), Path.GetFileName(path), path, "read from the command line");
                var result = DepthAnalyzer.Analyze(img, meta);
                if (result.VerdictSeverity == Severity.Alert) exit = Math.Max(exit, 1);
                entries.Add(new JsonReport.Entry(path, result, null));
            }
            catch (Exception ex)
            {
                exit = 2;
                entries.Add(new JsonReport.Entry(path, null, ex.Message));
                Console.Error.WriteLine($"ERROR {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        string doc = JsonReport.Report(entries, passCounts, histogram);
        if (outPath is not null) File.WriteAllText(outPath, doc);
        else Console.WriteLine(doc);
        return exit;
    }

    /// <summary>"200" or "60,120,240": pass counts to report against. Anything under 2 is dropped.</summary>
    private static int[] ParsePassList(string s)
    {
        var list = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => int.TryParse(x, System.Globalization.NumberStyles.Integer,
                                              System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0)
                    .Where(n => n >= 2)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
        return list.Length > 0 ? list : JsonReport.DefaultPassCounts;
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

        if (rest.Contains("--mass")) return RunMassCoupon(rest);

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
            WriteWarnings(pat.Warnings);
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
        AppendWarnings(sb, pat.Warnings);
        sb.AppendLine("Measure this coupon with a depth gauge, microscope or tilted-coupon photograph.");
        sb.AppendLine("Not with a scale: every feature weighs together. For mass loss, use --calibrate --mass.");
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
        for (int i = 0; i < spec.CombPitchUm.Length; i++)
        {
            double p = spec.CombPitchUm[i];
            bool drawn = i < pat.CombPitchDrawnUm.Length && pat.CombPitchDrawnUm[i] > 0;
            sb.AppendLine(drawn
                ? $"   {p,4:F0} um    resolved / merged   (circle one)"
                : $"   {p,4:F0} um    not drawn - finer than this resolution can represent");
        }
        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine("  ____________________________________________________________");
        sb.AppendLine("  ____________________________________________________________");
        return sb.ToString();
    }

    private static void WriteWarnings(List<string> warnings)
    {
        foreach (string w in warnings) Console.WriteLine("  WARNING  " + w);
        if (warnings.Count > 0) Console.WriteLine();
    }

    private static void AppendWarnings(StringBuilder sb, List<string> warnings)
    {
        if (warnings.Count == 0) return;
        sb.AppendLine("WARNINGS - read before cutting");
        foreach (string w in warnings) sb.AppendLine("  * " + w);
        sb.AppendLine();
    }

    /// <summary>
    /// Writes a mass-loss coupon and its worksheet.
    ///
    /// The worksheet is the substance here; the image is a plain square. It carries the
    /// controls that stop a session producing numbers that only look like results, a row per
    /// coupon, and the arithmetic from a weight difference to the removal efficiency the depth
    /// model runs on - so none of it has to be looked up at the bench.
    /// </summary>
    private static int RunMassCoupon(string[] rest)
    {
        var spec = new MassCouponSpec();
        string? outPath = null;

        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            string? Next() => i + 1 < rest.Length ? rest[++i] : null;
            switch (a)
            {
                case "--out": outPath = Next(); break;
                case "--coupon": if (double.TryParse(Next(), out double c)) spec.CouponMm = c; break;
                case "--zone": if (double.TryParse(Next(), out double z)) spec.ZoneMm = z; break;
                case "--thick": if (double.TryParse(Next(), out double t)) spec.ThicknessMm = t; break;
                case "--scale-g": if (double.TryParse(Next(), out double g)) spec.ScaleCapacityG = g; break;
                case "--rows": if (int.TryParse(Next(), out int rw)) spec.Rows = Math.Clamp(rw, 1, 60); break;
                case "--material": spec.Material = Next() ?? ""; break;
                case "--machine": spec.Machine = Next() ?? ""; break;
            }
        }

        try
        {
            var pat = CalibrationPattern.BuildMassCoupon(spec);
            string tag = string.Join("-", new[] { spec.Machine, spec.Material }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Replace(' ', '-').ToLowerInvariant()));
            outPath ??= $"depthview-mass-coupon{(tag.Length > 0 ? "-" + tag : "")}.png";

            PngEncoder.WriteGrey(outPath, pat.Pixels, pat.Width, pat.Height, 8, pat.Dpi, new[]
            {
                ("Software", $"DepthView {BuildInfo.Version}"),
                ("Comment", $"mass-loss coupon, {spec.CouponMm:F1} mm square, {pat.ZoneEdgeMm:F2} mm zone, " +
                            $"machine={spec.Machine}, material={spec.Material}"),
            });

            string sheet = Path.ChangeExtension(outPath, null) + "-worksheet.txt";
            File.WriteAllText(sheet, MassWorksheet(spec, pat));

            Console.WriteLine($"Mass-loss coupon -> {outPath}");
            WriteWarnings(pat.Warnings);
            Console.WriteLine($"  image           {pat.Width:N0} px square, {pat.PixelsPerMm:F0} px/mm, {pat.Dpi:F0} dpi, 8-bit");
            foreach (string line in pat.Legend) Console.WriteLine("  " + line);
            Console.WriteLine($"  worksheet       {Path.GetFileName(sheet)}  ({spec.Rows} coupon rows)");
            Console.WriteLine();
            Console.WriteLine("  Engrave the same image on every coupon, changing one setting between them.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Mass coupon failed: " + ex.Message);
            return 2;
        }
    }

    private static string MassWorksheet(MassCouponSpec spec, CalibrationPattern.Result pat)
    {
        var mat = CouponMaterial.Lookup(spec.Material);
        double area = pat.ZoneAreaMm2;
        string Blank(string s, int w) => s.Length > 0 ? s : new string('_', w);

        var sb = new StringBuilder();
        sb.AppendLine("DepthView mass-loss worksheet");
        sb.AppendLine("=============================");
        sb.AppendLine();
        sb.AppendLine($"Machine   : {Blank(spec.Machine, 20)}");
        sb.AppendLine(mat is { } m
            ? $"Material  : {m.Name}, density {m.Density:0.###} g/cm3"
            : $"Material  : {Blank(spec.Material, 20)}   density ________ g/cm3");
        sb.AppendLine("Source    : ____________________   rated ______ W   Lens: ____________________");
        sb.AppendLine("Software  : MakeIt / LightBurn (circle)          Date: ____________");
        sb.AppendLine();
        sb.AppendLine($"Coupon {spec.CouponMm:F1} mm square x {spec.ThicknessMm:F1} mm.  " +
                      $"Zone {pat.ZoneEdgeMm:F2} x {pat.ZoneEdgeMm:F2} mm = {area:F1} mm2.");
        if (mat is { } md)
            sb.AppendLine($"One milligram = {1000.0 / (md.Density * area):F2} um of average depth over the zone.");
        sb.AppendLine();
        AppendWarnings(sb, pat.Warnings);

        sb.AppendLine("BEFORE THE FIRST COUPON - skip these and the numbers below will look exactly");
        sb.AppendLine("like results without being any. (docs/DEPTH-PREDICTION.md, section 5.7)");
        sb.AppendLine();
        sb.AppendLine("  [ ] Scale warmed up and calibrated.  Check mass: start ________ g   end ________ g");
        sb.AppendLine("  [ ] Noise floor - one coupon weighed five times, lifted off the pan between:");
        sb.AppendLine("        ________  ________  ________  ________  ________ g    spread ______ mg");
        sb.AppendLine("  [ ] Blank-coupon control - an unengraved coupon through the whole clean/dry cycle:");
        sb.AppendLine("        before ________ g   after ________ g        (must not move)");
        sb.AppendLine("  [ ] Focus and spot (test T0) done: spot ______ um at focus with this lens");
        sb.AppendLine();
        sb.AppendLine("EACH COUPON: one setting. Engrave the zone only, clean, dry, let it reach room");
        sb.AppendLine("temperature, weigh on the marked spot of the pan, same orientation every time.");
        sb.AppendLine();
        sb.AppendLine("  #  ID   power  speed   interval  freq  pulse  passes | before      after       | loss");
        sb.AppendLine("          %      mm/s    mm        kHz   ns            | g           g           | mg");
        for (int i = 1; i <= spec.Rows; i++)
            sb.AppendLine($" {i,2}  ___  _____  ______  ________  ____  _____  ______ | ___________ ___________ | ______");
        sb.AppendLine();
        sb.AppendLine("Notes per coupon - colour, blackening, slag, visible hatch pattern, warp:");
        for (int i = 1; i <= spec.Rows; i++)
            sb.AppendLine($" {i,2}  ____________________________________________________________");
        sb.AppendLine();

        string dens = mat is { } mm ? mm.Density.ToString("0.###") : "density";
        sb.AppendLine("WORKING IT OUT, per coupon");
        sb.AppendLine();
        sb.AppendLine("  loss (mg)         = (before - after) x 1000");
        sb.AppendLine($"  volume (mm3)      = loss / {dens}        (g/cm3 is the same number as mg/mm3)");
        sb.AppendLine($"  mean depth (um)   = volume / {area:F1} x 1000");
        sb.AppendLine("  depth per pass    = mean depth / passes");
        sb.AppendLine("  energy per pass   E_DA (J/mm2) = P / (speed x interval),  P = power% x rated W");
        sb.AppendLine("  efficiency eta    (mm3/J) = depth per pass (mm) / E_DA");
        sb.AppendLine("                    cross-check: volume / (P x beam-on seconds)");
        sb.AppendLine();
        sb.AppendLine("  Speed in mm/s, interval in mm. MakeIt's \"line density\" is lines per CENTIMETRE:");
        sb.AppendLine("  interval = 10 / line density, so 300 -> 0.0333 mm.");
        sb.AppendLine("  Power % is assumed linear in watts until test T1 shows otherwise.");
        sb.AppendLine("  With crosshatch on, count each hatch direction as a pass - each one delivers E_DA.");
        sb.AppendLine();
        sb.AppendLine("  Stainless 304 check: published long-pulse settings give eta of about 0.7 to 2.7");
        sb.AppendLine("  x 10^-3 mm3/J. Far outside that, suspect power calibration or focus before the");
        sb.AppendLine("  physics. Brass, copper and aluminium have no published value to check against -");
        sb.AppendLine("  these coupons produce the first numbers, not a check on anyone else's.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ headless tuning

    /// <summary>
    /// --survey: what the tuning wizard measures, without opening it. Background or floor,
    /// placement, the artwork's own rim, the floor and top, pixel noise, and the nearly level
    /// areas with what each would do at a pass count. Changes nothing.
    /// </summary>
    private static int RunSurvey(string[] rest)
    {
        AttachParentConsole();
        string? input = rest.FirstOrDefault(a => !a.StartsWith('-'));
        bool json = rest.Contains("--json");
        int passes = Flag(rest, "--passes") is double p && p >= 2 ? (int)p : 256;
        if (input is null || !File.Exists(input))
        {
            if (json) Console.WriteLine(JsonReport.Error(JsonReport.SurveySchema, input is null ? "No input file." : $"No such file: {input}"));
            else Console.Error.WriteLine("Usage: DepthView --survey <image> [--passes n] [--json]");
            return 2;
        }

        var loaded = ImageLoader.Load(File.ReadAllBytes(input), Path.GetFileName(input), input, "survey");
        var grey = DepthTuner.ExtractGrey(loaded.Image);
        int max = loaded.Image.MaxValue;
        var s = DesignSurvey.Run(grey, loaded.Image.Width, loaded.Image.Height, max);

        if (json)
        {
            Console.WriteLine(JsonReport.Survey(Path.GetFullPath(input), s, passes));
            return 0;
        }

        var floor = s.Floor(s.BackgroundLooksLikeFloor);
        var top = s.Top(s.BackgroundLooksLikeFloor);
        Console.WriteLine($"Survey of {Path.GetFileName(input)}  ({s.Width:N0} x {s.Height:N0}, levels 0..{max:N0}, {s.Seconds:F2} s)");
        Console.WriteLine((s.SurroundShaded
                              ? $"  background      not one level, {s.BackgroundLow:N0}..{s.BackgroundHigh:N0} (followed from the edge; --uniform-surround evens it), {s.BackgroundShare * 100:F1}% of the image;"
                              : $"  background      level {s.Background:N0} around the edge, {s.BackgroundShare * 100:F1}% of the image;")
                        + $" {s.BackgroundInsideShare * 100:F1}% of the design's circle -> "
                        + (s.BackgroundLooksLikeFloor ? "looks like a cut-away floor" : "looks like a surround"));
        if (s.HasDesign)
            Console.WriteLine($"  design          centre {s.CentreX:F0}, {s.CentreY:F0}; radius {s.Radius:F0} px;"
                            + $" {s.OffCentrePx:F0} px from the canvas centre");
        Console.WriteLine(s.DrawnRim is { } rim
            ? $"  drawn rim       r {rim.Inner:F0}..{rim.Outer:F0} px ({rim.Width:F0} px), foot level {rim.FootLevel:F0}, top {rim.TopLevel:F0}"
            : "  drawn rim       none found");
        string Ends(bool bgIsDesign, bool rimCovered)
        {
            var f = s.Floor(bgIsDesign, rimCovered);
            var t = s.Top(bgIsDesign, rimCovered);
            string fs = f.Found
                ? $"floor {f.Low:N0}..{f.High:N0} ({f.Source}, {f.Share * 100:F1}%, roughness {f.Noise:N0}) -> black {f.Suggested:N0}"
                : f.Source == "gap"
                ? $"no floor; empty gap {f.Low:N0}..{f.High:N0} above {f.Share * 100:F2}% of pockets -> black {f.Suggested:N0}"
                : $"no floor, deepest 0.1% at {f.Suggested:N0}";
            string ts = t.Found
                ? $"top {t.Low:N0}..{t.High:N0} ({t.Source}, {t.Share * 100:F1}%, roughness {t.Noise:N0}) -> white {t.Suggested:N0}"
                : t.Source == "gap"
                ? $"no flat top; empty gap {t.Low:N0}..{t.High:N0} below {t.Share * 100:F2}% of peaks -> white {t.Suggested:N0}"
                : $"no flat top, highest 0.1% at {t.Suggested:N0}";
            return fs + "; " + ts;
        }
        foreach (bool bgIsDesign in new[] { s.BackgroundLooksLikeFloor, !s.BackgroundLooksLikeFloor })
            foreach (bool rimCovered in s.DrawnRim is null ? new[] { false } : new[] { false, true })
                Console.WriteLine($"  {(bgIsDesign ? "bg is floor" : "bg removed "),-11}{(rimCovered ? " rim covered" : "            ")}  {Ends(bgIsDesign, rimCovered)}");
        Console.WriteLine($"  pixel noise     about {s.NoiseSigma:F1} levels (Immerkaer, whole design; fine detail reads as noise too)");
        Console.WriteLine($"  flat areas      {s.FlatAreas.Count} found (slope under {FlatAreas.SlopeLimit * 100:F2}% of the range per pixel)");
        foreach (var a in s.FlatAreas)
            Console.WriteLine($"    #{a.Rank}  {a.Pixels,10:N0} px ({a.ShareOfDesign * 100:F1}%)  level {a.Median,6:N0}  spread {a.Spread,6:N0}"
                            + $"  jitter {a.Jitter,5:F0}  {(a.MostlyJitter ? "jitter" : "slope/dish")}"
                            + $"  crosses {a.BoundariesCrossed(passes, floor.Suggested, top.Suggested, max)} boundaries at {passes} passes"
                            + (a.TouchesFloor ? "  (floor)" : "") + (a.TouchesTop ? "  (top)" : ""));
        return 0;
    }

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

        string? input = null, outPath = null, maskPath = null, outlinePath = null;
        var o = new TuningOptions();
        bool haveBlack = false, haveWhite = false, wantOutline = false, json = false, noDpi = false;
        string? flatSpec = null, levelsFrom = null;
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

                // "--fit" on its own means content, which is the useful default. The word is
                // optional so the common case stays short, and naming it is how you ask for
                // the guarantee instead.
                case "--fit":
                    o.Fit = FitPolicy.Content;
                    if (i + 1 < rest.Length)
                    {
                        string? mode = rest[i + 1].ToLowerInvariant();
                        if (mode is "canvas") { o.Fit = FitPolicy.Canvas; i++; }
                        else if (mode is "design") { o.Fit = FitPolicy.Design; i++; }
                        else if (mode is "content") i++;
                    }
                    break;

                case "--pad":
                    if (Next() is "untouched") o.PadWith = PadFill.Untouched;
                    break;

                // Implies --fit design: covering the design's rim only means anything once the
                // blank is centred on the design.
                case "--cover-rim":
                    o.CoverDesignRim = true;
                    o.Fit = FitPolicy.Design;
                    break;

                // Takes an optional filename; bare --outline writes it beside the map.
                case "--outline":
                    wantOutline = true;
                    if (i + 1 < rest.Length && !rest[i + 1].StartsWith('-')) outlinePath = rest[++i];
                    break;
                case "--passes": if (int.TryParse(Next(), out int pp) && pp > 1) passes = pp; break;
                case "--dpi": if (double.TryParse(Next(), out double d)) o.Dpi = d; break;
                case "--blank": if (double.TryParse(Next(), out double bd2)) o.BlankDiameterMm = bd2; break;
                case "--rim-mm": if (double.TryParse(Next(), out double rmm)) o.RimWidthMm = rmm; break;
                case "--ramp-mm": if (double.TryParse(Next(), out double ramm)) o.RimRampMm = ramm; break;
                case "--spot": if (double.TryParse(Next(), out double sp)) spotMicrons = sp; break;
                case "--depth-mm": if (double.TryParse(Next(), out double dm)) o.TargetDepthMm = dm; break;
                case "--bits": if (int.TryParse(Next(), out int bd)) o.OutputBitDepth = bd; break;
                case "--json": json = true; break;
                case "--no-dpi": noDpi = true; break;
                case "--uniform-surround": o.UniformSurround = true; break;

                // The wizard's choices, from the command line. --flat takes one word per flat
                // area, largest first (leave, smooth, flatten), as the wizard found them.
                // --levels-from picks level points the way the wizard suggests them: from the
                // design without its surround, or with the background counted as a floor.
                case "--flat": flatSpec = Next(); break;
                case "--levels-from": levelsFrom = Next()?.ToLowerInvariant(); break;
                default:
                    if (!a.StartsWith('-') && input is null) input = a;
                    break;
            }
        }

        if (input is null || !File.Exists(input))
        {
            if (json) Console.WriteLine(JsonReport.Error(JsonReport.TuneSchema,
                input is null ? "No input file." : $"No such file: {input}"));
            Console.Error.WriteLine("Usage: DepthView --tune <image> [options]. See --help.");
            return 2;
        }

        outPath ??= Path.ChangeExtension(input, null) + "-tuned.png";

        // Never write over the original - no exceptions. A caller that builds paths for us (a
        // host program, a script) is exactly the one that can get this wrong without noticing.
        if (SamePath(input, outPath)
            || (maskPath is not null && SamePath(input, maskPath))
            || (outlinePath is not null && SamePath(input, outlinePath)))
        {
            const string refuse = "Refusing to write over the input file. Give --out a different path.";
            if (json) Console.WriteLine(JsonReport.Error(JsonReport.TuneSchema, refuse));
            Console.Error.WriteLine(refuse);
            return 2;
        }

        // With --json the human-readable lines below are swallowed and one JSON document is
        // written at the end instead, so a caller can parse stdout whole.
        var realOut = Console.Out;
        if (json) Console.SetOut(TextWriter.Null);
        string? maskWritten = null, outlineWritten = null;

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

            // The wizard's measurements, when anything asks for them.
            DesignSurvey? survey = flatSpec is not null || levelsFrom is not null
                ? DesignSurvey.Run(grey, loaded.Image.Width, loaded.Image.Height, maxValue)
                : null;
            if (survey is not null && levelsFrom is "design" or "floor")
            {
                bool bgIsDesign = levelsFrom == "floor";
                sb = survey.Floor(bgIsDesign, o.CoverDesignRim).Suggested;
                sw = survey.Top(bgIsDesign, o.CoverDesignRim).Suggested;
            }
            if (!haveBlack) o.BlackPoint = sb;
            if (!haveWhite) o.WhitePoint = sw;

            if (survey is not null && flatSpec is not null)
            {
                var words = flatSpec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < words.Length && k < survey.FlatAreas.Count; k++)
                {
                    var mode = words[k].ToLowerInvariant() switch
                    {
                        "flatten" => FlatMode.Flatten,
                        "smooth" => FlatMode.Smooth,
                        _ => FlatMode.Leave,
                    };
                    o.FlatActions.Add(survey.FlatAreas[k].ToAction(mode, maxValue));
                }
            }

            // Millimetres win over percentages when both are given: one came off a pair of
            // calipers and the other is a guess.
            o.ResolvePhysical(loaded.Image.Width, loaded.Image.Height);
            if (noDpi) o.Dpi = null;
            if (rimPct is double pct && o.RimWidthMm is null)
            {
                double half = Math.Min(loaded.Image.Width, loaded.Image.Height) / 2.0;
                o.RimRadius = half * (1 - pct / 100.0);
                o.RimRamp = half * ((rampPct ?? pct) / 100.0);
            }

            var tuned = DepthTuner.Apply(grey, loaded.Image.Width, loaded.Image.Height,
                                         maxValue, o, out var rep);

            // Fitting grows the canvas, so the output is not always the size of the input.
            // The report carries the dimensions that were actually produced.
            int outW = rep.OutWidth, outH = rep.OutHeight;

            // Shared with the Tune window, deliberately: a file written from the dialog and one
            // written here with the same settings are the same bytes, which is only true while
            // there is one implementation of "write it out".
            TuneJob.WriteTuned(outPath, tuned, outW, outH, maxValue, o, Path.GetFileName(input));

            if (maskPath is not null && o.AddRim)
            {
                TuneJob.WriteRimMask(maskPath, outW, outH, o);
                maskWritten = maskPath;
                Console.WriteLine($"  mask            {Path.GetFileName(maskPath)} (white = engraved area)");
            }

            // The vector the framer can actually see. A depth map cannot help here at all: an
            // image frames as its own rectangle whatever is drawn inside it, so lining a round
            // design up with a round blank needs a circle the laser can trace.
            if (wantOutline && o.BlankDiameterMm is double blankMm && blankMm > 0)
            {
                outlinePath ??= Path.ChangeExtension(outPath, null) + "-outline.svg";
                double engraveMm = o.RimRadius > 0 ? o.RimRadius * 2 / (Math.Min(outW, outH) / blankMm) : 0;
                AlignmentOutline.Write(outlinePath, blankMm, engraveMm, Path.GetFileName(outPath));
                outlineWritten = outlinePath;

                Console.WriteLine($"  outline         {Path.GetFileName(outlinePath)} - "
                                + $"{blankMm:F1} mm circle to frame against the blank's rim");
                Console.WriteLine("                  put it on a tool layer, turn Frame off for the image");
                Console.WriteLine("                  layer, and frame with Hull or Contour rather than Bounds.");
            }

            // Re-analyse what was written. The tool marking its own homework is the point:
            // the claim that tuning helped should be a measurement, not an assertion.
            var reloaded = ImageLoader.Load(File.ReadAllBytes(outPath), Path.GetFileName(outPath), outPath, "tuned");
            var after = DepthAnalyzer.Analyze(reloaded.Image, reloaded.Meta);

            var (dBefore, _) = before.SlicesAt(passes);
            var (dAfter, _) = after.SlicesAt(passes);
            var pBefore = before.PassesAt(passes);
            var pAfter = after.PassesAt(passes);

            Console.WriteLine($"Tuned {Path.GetFileName(input)} -> {Path.GetFileName(outPath)}");
            // Against the canvas that was written, not the one that was read: a fit changes how
            // many pixels the blank spans, and so every millimetre figure below.
            if (o.PixelsPerMm(outW, outH) is double ppmm)
            {
                var check = ResolutionCheck.For(1000.0 / ppmm, spotMicrons);
                Console.WriteLine($"  physical        {o.BlankDiameterMm:F1} mm across {Math.Min(outW, outH):N0} px"
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

                    // Two different things share one pixel value in the output, and it is
                    // worth saying so: the rim is part of the coin and is deliberately left
                    // uncut, while the corners are not on the coin at all. Both are white
                    // because white is the only way a depth map can say "no passes here", and
                    // that is the correct instruction for both - but they are not the same
                    // thing, and a reader looking at a white square should know which is which.
                    double blankPx = Math.Min(rep.OutWidth, rep.OutHeight) / 2.0;
                    double cornerArea = 1 - Math.PI / 4;
                    Console.WriteLine($"                  white beyond r={o.RimRadius:F0} px covers both the"
                                    + $" {rw:F2} mm rim ring and the");
                    Console.WriteLine($"                  corners outside the blank (r>{blankPx:F0} px,"
                                    + $" {cornerArea * 100:F0}% of the square). Both get zero");
                    Console.WriteLine("                  passes, which is right for both, but only the rim is on the coin.");

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

            if (rep.Fit is { Recentred: true } centred)
            {
                Console.WriteLine($"  fitted          blank centred on the design; {loaded.Image.Width:N0} x {loaded.Image.Height:N0}"
                                + $" -> {centred.Size:N0} px square, "
                                + (centred.Crops(loaded.Image.Width, loaded.Image.Height)
                                    ? $"cropping background only ({rep.FitDesignLost:N0} px of design removed)"
                                    : "padded")
                                + "; no pixel resampled");
                Console.WriteLine($"                  the design spans {centred.ArtAcrossMm:F1} mm of the"
                                + $" {o.BlankDiameterMm:F1} mm blank, at {centred.PixelsPerMm:F1} px/mm");
                if (centred.CoveredRim is { } own)
                    Console.WriteLine($"  design rim      found r {own.Inner:F0}..{own.Outer:F0} px from the design's centre"
                                    + $" ({own.Width / centred.PixelsPerMm:F2} mm wide, level {own.FootLevel:F0} at its foot,"
                                    + $" {own.TopLevel:F0} at its top); now under the new rim, {rep.DesignRimCovered:N0} px covered");
                else if (o.CoverDesignRim)
                    Console.WriteLine("  design rim      none found, so nothing was covered: the design sits inside the new rim");
            }
            else if (rep.Fit is { } fit)
            {
                Console.WriteLine($"  fitted          canvas grown {loaded.Image.Width:N0} -> {fit.Size:N0} px"
                                + $" so the design clears the rim; no pixel resampled");
                Console.WriteLine($"                  the artwork now spans {fit.ArtAcrossMm:F1} mm of the"
                                + $" {o.BlankDiameterMm:F1} mm blank, at {fit.PixelsPerMm:F1} px/mm");
            }

            if (o.AddRim) Console.WriteLine($"  rim             {rep.Summary}");
            if (rep.FlatChanged > 0)
                Console.WriteLine($"  flat areas      {o.FlatActions.Count(a => a.Mode == FlatMode.Flatten)} flattened,"
                                + $" {o.FlatActions.Count(a => a.Mode == FlatMode.Smooth)} smoothed:"
                                + $" {rep.FlatChanged:N0} px moved, by at most {rep.FlatMaxChange:N0} levels");
            if (rep.FitDesignLost > 0)
                Console.WriteLine($"                  centring on the design would have cropped {rep.FitDesignLost:N0} px"
                                + " of it, so it was not done");
            if (o.AddRim && o.Fit != FitPolicy.Design && rep.DesignOffCentreMm is >= DepthTuner.OffCentreNoteMm)
                Console.WriteLine($"                  the design sits {rep.DesignOffCentreMm:F1} mm off the blank's centre;"
                                + " --fit design centres the blank on it");
            Console.WriteLine($"  changed         {rep.Changed:N0} of {grey.Length:N0} pixels");
            Console.WriteLine($"  depths @ {passes,-4}   {dBefore:N0} -> {dAfter:N0}");
            Console.WriteLine($"  passes          relief {pBefore.Relief:N0} -> {pAfter.Relief:N0}, "
                            + $"uniform {pBefore.Uniform:N0} -> {pAfter.Uniform:N0}, "
                            + $"empty {pBefore.Empty:N0} -> {pAfter.Empty:N0}");

            // Said out loud because the numbers above invite the wrong reading. Stretching
            // makes the relief deeper; it does not recover resolution that was being thrown
            // away. Levels per unit of depth are the same before and after.
            if (pAfter.Relief > pBefore.Relief)
                Console.WriteLine($"                  the relief is now {(double)pAfter.Relief / Math.Max(1, pBefore.Relief):F1}x deeper."
                                + " More depths because it is deeper, not because");
            if (pAfter.Relief > pBefore.Relief)
                Console.WriteLine("                  resolution was being wasted. If the narrow range was deliberate,"
                                + " this overrides it.");
            Console.WriteLine($"  range use       {before.RangeUtilisation * 100:F1}% -> {after.RangeUtilisation * 100:F1}%");

            if (json)
            {
                realOut.WriteLine(JsonReport.Tune(new JsonReport.TuneOutcome
                {
                    Input = input,
                    Output = outPath,
                    Mask = maskWritten,
                    Outline = outlineWritten,
                    InWidth = loaded.Image.Width,
                    InHeight = loaded.Image.Height,
                    Options = o,
                    Report = rep,
                    Passes = passes,
                    SpotMicrons = spotMicrons,
                    Before = before,
                    After = after,
                }));
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (json) realOut.WriteLine(JsonReport.Error(JsonReport.TuneSchema, "Tune failed: " + ex.Message));
            Console.Error.WriteLine("Tune failed: " + ex.Message);
            return 2;
        }
        finally
        {
            if (json) Console.SetOut(realOut);
        }
    }

    /// <summary>Whether two paths name the same file, allowing for how each OS compares names.</summary>
    internal static bool SamePath(string a, string b)
    {
        try
        {
            var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), cmp);
        }
        catch { return false; }
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
        double exagStops = 0, az = 315, el = 42, ao = 1, yaw = 0, pitch = 62, zoomMul = 1;
        double? blankArg = null, thickArg = null, depthArg = null;
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
                case "--exag": exagStops = D(rest, ref i, 0); break;
                case "--blank": blankArg = D(rest, ref i, 0); break;
                case "--thick": thickArg = D(rest, ref i, 0); break;
                case "--depth-mm": depthArg = D(rest, ref i, 0); break;
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

        // Through the shared blank rather than around it, so a render and the windows agree on
        // what an omitted flag means - including depth following a --thick that was given.
        Blank.Current.ApplyTransient(blankArg, thickArg, depthArg);
        double blankMm = Blank.Current.DiameterMm;
        double depthMm = Blank.Current.TargetDepthMm;
        double thickMm = Blank.Current.ThicknessMm;

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
                Exaggeration = Rendering.ZScale.RendererExaggeration(
                    Rendering.ZScale.DrawnDepthMm(depthMm, exagStops), blankMm, fw, fh),
                SlabRatio = thickMm / depthMm,
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

            // A PNG carries no badge, so the console says what scale it was drawn at.
            double drawn = Rendering.ZScale.DrawnDepthMm(depthMm, exagStops);
            Console.WriteLine($"{Rendering.ZScale.BadgeText(exagStops)}: {depthMm:0.00#} mm target " +
                              $"drawn {drawn:0.00#} mm deep on a {blankMm:0.#} x {thickMm:0.0#} mm blank");
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

        // Attaching is only half of it. A windowed executable starts with no valid standard
        // handles, so .NET has already bound Console.Out to a writer that discards everything -
        // and it stays bound after AttachConsole succeeds. Worse, it stays bound when stdout
        // was redirected to a file or a pipe, which is why "DepthView --report x.png > out.txt"
        // produced an empty file rather than a report.
        //
        // Reopening the standard streams here fixes both cases at once: the handle is the
        // console when there is one and the redirection target when there is not.
        try
        {
            var so = Console.OpenStandardOutput();
            if (so != Stream.Null)
                Console.SetOut(new StreamWriter(so) { AutoFlush = true });

            var se = Console.OpenStandardError();
            if (se != Stream.Null)
                Console.SetError(new StreamWriter(se) { AutoFlush = true });
        }
        catch
        {
            // Nowhere to write at all. Every path that prints also supports --out.
        }
    }
}
