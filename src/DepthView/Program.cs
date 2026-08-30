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
using SixLabors.ImageSharp;   // for the SaveAsPng extension on the headless render path

namespace DepthView;

internal static class Program
{
    /// <summary>File passed on the command line, loaded once the window opens.</summary>
    public static string? StartupFile;

    /// <summary>Open the relief preview straight away, alongside the analysis window.</summary>
    public static bool StartupRelief;

    private const string Help = """
        DepthView - depth map candidate inspector

          DepthView                       open the window
          DepthView <image>               open the window with that image loaded
          DepthView <image> --relief      also open the 3D relief preview
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

        int ridx = Array.FindIndex(args, a => a is "--render");
        if (ridx >= 0) return RunRender(args.Skip(ridx + 1).ToArray());

        int idx = Array.FindIndex(args, a => a is "-r" or "--report");
        if (idx >= 0) return RunReport(args.Skip(idx + 1).ToArray());

        StartupFile = args.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
        StartupRelief = args.Any(a => a is "--relief" or "-3d");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
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
