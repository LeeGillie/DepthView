using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using DepthView.Finishing;
using DepthView.Imaging;
using DepthView.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DepthView.Analysis;

/// <summary>Everything the job report is made from.</summary>
public sealed class JobReportInput
{
    public required string MapName { get; init; }
    public string? MapPath { get; init; }
    public required ImageData Image { get; init; }
    public required AnalysisResult Before { get; init; }
    public required ushort[] Grey { get; init; }
    /// <summary>The tuning as it will be written - already resolved to pixels.</summary>
    public required TuningOptions Options { get; init; }
    public int Passes { get; init; } = 256;
    public double SpotMicrons { get; init; } = 7;
    public double BlankMm { get; init; } = Blank.DefaultDiameterMm;
    public double ThicknessMm { get; init; } = Blank.DefaultThicknessMm;
    public double DepthMm { get; init; } = 0.72;
    public bool DepthFollowsThickness { get; init; }
    public string? LaserType { get; init; }
    public string? Lens { get; init; }
    public string? Material { get; init; }
    public string? Notes { get; init; }
    public FinishRecipe? Finish { get; init; }
    public string? ProfilePath { get; init; }
    public bool WizardUsed { get; init; }
}

/// <summary>
/// The job report (TODO 10.2): one self-contained HTML page - pictures and charts inline - to
/// open in a browser and print or save as PDF. No new library, the same on every platform.
/// It says what the map is, what the blank and laser are, every tuning setting, what the job
/// will cut, and the finishing recipe with its safety notes. A preview is a look, not a
/// prediction, and the page says so.
/// </summary>
public static class JobReport
{
    public static string LaserName(string? id) => id switch
    {
        "uv" => "UV",
        "mopa" => "MOPA fibre",
        "fiber" => "Q-switched fibre",
        "co2" => "CO2",
        "diode" => "Diode",
        null or "" => "not recorded",
        _ => id,
    };

    public static string Build(JobReportInput i, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var o = i.Options;
        var r = i.Before;
        int w = i.Image.Width, h = i.Image.Height, max = i.Image.MaxValue;

        // The file as it would be written, measured the same way the Tune window measures it.
        var tuned = DepthTuner.Apply(i.Grey, w, h, max, o, out var rep);
        int tw = rep.OutWidth, th = rep.OutHeight, tmax = max;
        if (o.OutputBitDepth == 8 && max != 255) { tuned = TuneJob.ScaleForOutput(tuned, max, 8); tmax = 255; }
        ct.ThrowIfCancellationRequested();
        var tunedImg = new ImageData { Width = tw, Height = th, Channels = 1, BitDepth = tmax == 255 ? 8 : 16, MaxValue = tmax, Samples = tuned };
        var after = DepthAnalyzer.Analyze(tunedImg, new ImageMetadata { Format = "PNG", ColorModel = "Grayscale", DeclaredBitDepth = tunedImg.BitDepth, DeclaredChannels = 1 });
        ct.ThrowIfCancellationRequested();

        double ppO = Math.Min(w, h) / i.BlankMm, ppT = Math.Min(tw, th) / i.BlankMm;
        var terO = TerraceMap.Measure(i.Grey, w, h, max, i.Passes, ppO, i.DepthMm, i.SpotMicrons, false, ct);
        var terT = TerraceMap.Measure(tuned, tw, th, tmax, i.Passes, ppT, i.DepthMm, i.SpotMicrons, true, ct);
        const int Thumb = 440;
        string origPng = GreyPng(i.Grey, w, h, max, Thumb);
        string tunedPng = GreyPng(tuned, tw, th, tmax, Thumb);
        int ow = Thumb, oh = Math.Max(1, (int)Math.Round(Thumb * (double)th / tw));
        string terPng = BgraPng(TerraceMap.Overlay(tuned, tw, th, tmax, terT.Classes!, ow, oh, terT.BlankRadiusPx), ow, oh);
        terT.Classes = null;
        ct.ThrowIfCancellationRequested();

        string reliefO = Relief(i.Image, i, null, out _);
        string reliefT = Relief(tunedImg, i, null, out _);
        FinishResult? fin = null;
        string? finished = i.Finish is null ? null : Relief(tunedImg, i, i.Finish, out fin);
        ct.ThrowIfCancellationRequested();

        var (dB, _) = r.SlicesAt(i.Passes);
        var (dA, _) = after.SlicesAt(i.Passes);
        var pB = r.PassesAt(i.Passes);
        var pA = after.PassesAt(i.Passes);

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>Job report - {E(i.MapName)}</title><style>{Css}</style></head><body><main>");

        sb.Append("<header><div><div class=\"kicker\">DepthView job report</div>");
        sb.Append($"<h1>{E(i.MapName)}</h1>");
        sb.Append($"<p class=\"sub\">{E(DateTime.Now.ToString("d MMMM yyyy, HH:mm", inv))} &middot; DepthView {E(BuildInfo.Version)}"
                + (i.ProfilePath is null ? "" : $" &middot; settings {E(Path.GetFileName(i.ProfilePath))}") + "</p></div>");
        sb.Append($"<div class=\"verdict {Sev(r.VerdictSeverity)}\"><b>{E(r.Verdict)}</b><span>{E(r.VerdictDetail)}</span></div></header>");

        // ---- the job at a glance
        sb.Append("<section><h2>The job</h2><div class=\"grid3\">");
        sb.Append(Card("Workpiece", new[]
        {
            ("Shape", "round blank"),
            ("Diameter", $"{i.BlankMm:0.0#} mm"),
            ("Thickness", $"{i.ThicknessMm:0.0#} mm"),
            ("Target depth", $"{i.DepthMm:0.00#} mm" + (i.DepthFollowsThickness ? $" ({i.DepthMm / i.ThicknessMm * 100:0}% of the thickness, automatic)" : " (set by hand)")),
            ("Material", string.IsNullOrWhiteSpace(i.Material) ? "not recorded" : i.Material!),
        }));
        sb.Append(Card("Laser", new[]
        {
            ("Type", LaserName(i.LaserType)),
            ("Lens", string.IsNullOrWhiteSpace(i.Lens) ? "not recorded" : i.Lens!),
            ("Spot", $"{i.SpotMicrons:0.#} um"),
            ("Passes", $"{i.Passes:N0}"),
            ("Depth per pass", $"{i.DepthMm * 1000 / i.Passes:0.0#} um"),
        }));
        sb.Append(Card("Map", new[]
        {
            ("File", $"{w:N0} x {h:N0} px, {i.Image.BitDepth}-bit"),
            ("Grey levels", $"{r.UniqueGreyLevels:N0}"),
            ("Resolution", $"{1000.0 / ppO:0.0} um per pixel against a {i.SpotMicrons:0} um spot"),
            ("Written as", $"{tw:N0} x {th:N0} px, {(tmax == 255 ? 8 : 16)}-bit"),
            ("Tuned levels", $"{after.UniqueGreyLevels:N0}"),
        }));
        sb.Append("</div></section>");

        // ---- pictures
        sb.Append("<section class=\"keep\"><h2>Before and after</h2><div class=\"grid3\">");
        sb.Append(Fig(origPng, "The map as it is"));
        sb.Append(Fig(tunedPng, "As it will be written"));
        sb.Append(Fig(terPng, $"Where the layers will show at {i.Passes:N0} passes - amber: treads wider than the spot, red: wider than three"));
        sb.Append("</div><div class=\"grid3\">");
        sb.Append(Fig(reliefO, "Lit relief, as it is (drawn 2x deep)"));
        sb.Append(Fig(reliefT, "Lit relief, tuned (drawn 2x deep)"));
        if (finished is not null) sb.Append(Fig(finished, "Finished, as the recipe below would leave it - a look, not a prediction"));
        sb.Append("</div></section>");

        // ---- what the job will do
        sb.Append("<section class=\"keep\"><h2>What the job will cut</h2><table class=\"cmp\"><tr><th></th><th>As it is</th><th>Tuned</th></tr>");
        Row(sb, $"Distinct depths at {i.Passes:N0} passes", $"{dB:N0}", $"{dA:N0}");
        Row(sb, "Passes forming relief", $"{pB.Relief:N0}", $"{pA.Relief:N0}");
        Row(sb, "Passes cutting a flat recess", $"{pB.Uniform:N0}", $"{pA.Uniform:N0}");
        Row(sb, "Passes cutting nothing", $"{pB.Empty:N0}", $"{pA.Empty:N0}");
        Row(sb, "Range used", $"{r.RangeUtilisation * 100:0}%", $"{after.RangeUtilisation * 100:0}%");
        Row(sb, "Layer edges that will show", $"{terO.ShareWider * 100:0}%", $"{terT.ShareWider * 100:0}%");
        Row(sb, "Median tread", $"{terO.MedianTreadMicrons:0} um", $"{terT.MedianTreadMicrons:0} um");
        sb.Append("</table>");
        sb.Append($"<h3>Depth across the middle</h3>{DepthLine(tuned, tw, th, tmax, i.Passes, i.DepthMm, i.BlankMm)}");
        sb.Append("<p class=\"note\">The tuned surface along a line through the centre, and the staircase it is cut as at this pass count. Depth is the target depth; how deep a pass really cuts depends on the laser settings.</p>");
        sb.Append($"<h3>Source levels</h3>{Histogram(r.GreyHistogram, max, o.BlackPoint, o.WhitePoint)}");
        sb.Append("<p class=\"note\">Shaded ends are the levels the black and white points flatten: below black becomes one full depth, above white is left untouched.</p>");
        sb.Append("</section>");

        // ---- every setting
        sb.Append("<section class=\"keep\"><h2>Tuning settings</h2><table class=\"kv\">");
        KV(sb, "Black point", $"{o.BlackPoint:N0}");
        KV(sb, "White point", $"{o.WhitePoint:N0}");
        KV(sb, "Stretch between them", o.Stretch ? "yes" : "no");
        KV(sb, "Invert", o.Invert ? "yes (art authored white-deepest)" : "no");
        KV(sb, "Even out a shaded surround", o.UniformSurround ? "yes" : "no");
        KV(sb, "Rim", o.AddRim ? $"{o.RimWidthMm:0.00} mm, ramp {(o.RimRampMm ?? 0):0.00} mm" : "none");
        KV(sb, "Fit the design inside the rim", o.Fit switch
        {
            FitPolicy.Content => "artwork (measured)",
            FitPolicy.Canvas => "whole image (corners)",
            FitPolicy.Design => "artwork, centred on it" + (o.CoverDesignRim ? ", replacing its own rim" : ""),
            _ => "no",
        });
        if (o.Fit != FitPolicy.None) KV(sb, "New space", o.PadWith == PadFill.Untouched ? "left untouched" : "matches the background");
        int flat = o.FlatActions.Count(a => a.Mode == FlatMode.Flatten), smooth = o.FlatActions.Count(a => a.Mode == FlatMode.Smooth);
        KV(sb, "Nearly level areas", flat + smooth == 0 ? "left as drawn" : $"{flat} flattened, {smooth} smoothed");
        KV(sb, "Quantise to the pass count", o.Slices > 0 ? (o.Dither ? "yes, boundaries dithered" : "yes") : "no");
        KV(sb, "Output", $"{o.OutputBitDepth}-bit PNG" + (o.Dpi is double dpi ? $", true size stamped ({dpi:0} dpi)" : ""));
        KV(sb, "Came from", i.WizardUsed ? "the tuning wizard's answers, then this window" : "this window");
        sb.Append("</table>");
        if (rep.Changed > 0)
            sb.Append($"<p class=\"note\">{rep.Changed:N0} of {i.Grey.Length:N0} pixels change. {E(rep.Summary)}</p>");
        sb.Append("</section>");

        // ---- findings
        var worth = r.Findings.Where(f => f.Severity >= Severity.Warn).ToList();
        if (worth.Count > 0)
        {
            sb.Append("<section><h2>What the analysis flagged</h2><ul class=\"findings\">");
            foreach (var f in worth) sb.Append($"<li class=\"{Sev(f.Severity)}\"><b>{E(f.Title)}</b> {E(f.Detail)}</li>");
            sb.Append("</ul></section>");
        }

        // ---- finishing
        if (i.Finish is { } recipe)
        {
            var cat = FinishCatalogue.Builtin;
            sb.Append("<section class=\"keep\"><h2>Finishing</h2><table class=\"kv\">");
            foreach (var line in recipe.Describe(cat).Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (t.Length > 14) KV(sb, t[..14].Trim(), t[14..].Trim());
            }
            sb.Append("</table>");
            var d = cat.Darkener(recipe.Darkener);
            if (!d.IsNone && d.Hazard is { } hz)
                sb.Append($"<div class=\"hazard {E(hz.Level)}\"><b>{E(hz.Badge)}</b> {E(hz.Text)}</div>");
            if (fin is not null && fin.Warnings.Count > 0)
            {
                sb.Append("<ul class=\"findings\">");
                foreach (var wn in fin.Warnings) sb.Append($"<li class=\"warn\">{E(wn)}</li>");
                sb.Append("</ul>");
            }
            sb.Append("<p class=\"note\">Products are named as examples so they can be found; none has endorsed DepthView. Read the maker's safety data sheet before use.</p></section>");
        }

        if (!string.IsNullOrWhiteSpace(i.Notes))
            sb.Append($"<section><h2>Notes</h2><p class=\"notes\">{E(i.Notes!).Replace("\n", "<br>")}</p></section>");

        sb.Append($"<footer>DepthView {E(BuildInfo.Version)} &middot; {E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", inv))} &middot; "
                + "Nothing in this report changes the map. A preview is a look, not a prediction: how deep each pass cuts depends on the laser, its settings and the material.</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>Write the report beside the map, or where asked. Never over an image.</summary>
    public static string Write(string html, string path)
    {
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The job report is written as .html.");
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    public static string PathBeside(string mapPath) => Path.ChangeExtension(mapPath, null) + "-job-report.html";

    // ------------------------------------------------------------------ pictures

    private static string Relief(ImageData img, JobReportInput i, FinishRecipe? recipe, out FinishResult? fin)
    {
        fin = null;
        var field = Rendering.ReliefRenderer.BuildHeights(img, 900, out int fw, out int fh);
        var scene = new Rendering.ReliefScene(field, fw, fh);
        var m = Rendering.MaterialLibrary.Presets.FirstOrDefault(p => p.Name.Contains("brass", StringComparison.OrdinalIgnoreCase))
                ?? Rendering.MaterialLibrary.Presets[0];
        int w = 520, h = 400;
        var o = new Rendering.ReliefOptions
        {
            Material = m,
            LightAzimuthDeg = 315,
            LightElevationDeg = 42,
            AoStrength = 1,
            Exaggeration = Rendering.ZScale.RendererExaggeration(Rendering.ZScale.DrawnDepthMm(i.DepthMm, 1), i.BlankMm, fw, fh),
            SlabRatio = i.ThicknessMm / i.DepthMm,
            Zoom = Math.Min((double)w / fw, (double)h / fh) * 0.9,
            Quality = 1,
            Orbit = true,
            YawDeg = 24,
            PitchDeg = 42,
            MeshResolution = 560,
            Supersample = 2,
        };
        if (recipe is not null)
        {
            var cat = FinishCatalogue.Builtin;
            var f = FinishSimulator.Run(cat, recipe, field, fw, fh, i.BlankMm / Math.Min(fw, fh), i.DepthMm);
            fin = f;
            o.Finish = f.Layer;
            o.Material = new Rendering.MaterialPreset { Name = "Finishing", Metallic = true };
        }
        var buf = new byte[(long)w * h * 4];
        Rendering.ReliefRenderer.Render(buf, w, h, scene, o);
        return BgraPng(buf, w, h);
    }

    private static string GreyPng(ushort[] g, int w, int h, int max, int side)
    {
        double s = Math.Max(w, h) / (double)side;
        int ow = Math.Max(1, (int)Math.Round(w / s)), oh = Math.Max(1, (int)Math.Round(h / s));
        var px = new byte[ow * oh];
        for (int y = 0; y < oh; y++)
        {
            int y0 = (int)(y * s), y1 = Math.Max(y0 + 1, Math.Min(h, (int)((y + 1) * s)));
            for (int x = 0; x < ow; x++)
            {
                int x0 = (int)(x * s), x1 = Math.Max(x0 + 1, Math.Min(w, (int)((x + 1) * s)));
                long sum = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++) { sum += g[(long)yy * w + xx]; n++; }
                px[y * ow + x] = (byte)Math.Round(sum * 255.0 / Math.Max(1, n) / Math.Max(1, max));
            }
        }
        using var img = Image.LoadPixelData<L8>(px, ow, oh);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static string BgraPng(byte[] bgra, int w, int h)
    {
        using var img = Image.LoadPixelData<Bgra32>(bgra, w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    // ------------------------------------------------------------------ charts

    private static string DepthLine(ushort[] g, int w, int h, int max, int passes, double depthMm, double blankMm)
    {
        int y = h / 2;
        double cx = (w - 1) / 2.0, rad = Math.Min(w, h) / 2.0;
        int x0 = Math.Max(0, (int)Math.Ceiling(cx - rad)), x1 = Math.Min(w - 1, (int)Math.Floor(cx + rad));
        int n = Math.Max(2, x1 - x0 + 1);
        const double W = 720, H = 170, L = 46, T = 8, B = 24;
        double pw = W - L - 8, ph = H - T - B;
        var surf = new StringBuilder();
        var stair = new StringBuilder();
        int step = Math.Max(1, n / 720);
        for (int k = 0; k < n; k += step)
        {
            int x = x0 + k;
            double v = g[(long)y * w + x] / (double)Math.Max(1, max);
            double d = (1 - v) * depthMm;
            double q = Math.Ceiling((1 - v) * passes - 1e-9) / passes * depthMm;
            double px = L + k * pw / (n - 1);
            surf.Append(px.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append((T + d / depthMm * ph).ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
            stair.Append(px.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append((T + q / depthMm * ph).ToString("0.#", CultureInfo.InvariantCulture)).Append(' ');
        }
        var inv = CultureInfo.InvariantCulture;
        return $"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Depth across the middle\">"
             + $"<line x1=\"{L}\" y1=\"{T}\" x2=\"{W - 8}\" y2=\"{T}\" class=\"axis\"/><line x1=\"{L}\" y1=\"{T + ph}\" x2=\"{W - 8}\" y2=\"{T + ph}\" class=\"axis\"/>"
             + $"<text x=\"{L - 6}\" y=\"{T + 4}\" text-anchor=\"end\">0</text><text x=\"{L - 6}\" y=\"{T + ph + 4}\" text-anchor=\"end\">{depthMm.ToString("0.00", inv)}</text>"
             + $"<text x=\"{L}\" y=\"{H - 6}\">0</text><text x=\"{W - 8}\" y=\"{H - 6}\" text-anchor=\"end\">{blankMm.ToString("0.#", inv)} mm across</text>"
             + $"<polyline points=\"{stair}\" class=\"stair\"/><polyline points=\"{surf}\" class=\"surf\"/>"
             + $"<text x=\"{L + 8}\" y=\"{T + ph - 8}\" class=\"key surfk\">surface</text><text x=\"{L + 72}\" y=\"{T + ph - 8}\" class=\"key stairk\">as cut at {passes:N0} passes</text>"
             + "</svg>";
    }

    private static string Histogram(long[] hist, int max, int black, int white)
    {
        const int Bins = 256;
        var bins = new double[Bins];
        for (int v = 0; v <= max && v < hist.Length; v++) bins[(int)((long)v * Bins / (max + 1))] += hist[v];
        double top = Math.Log10(1 + bins.Max());
        const double W = 720, H = 120, T = 6, B = 18;
        double ph = H - T - B, bw = W / Bins;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Source grey-level histogram, log scale\">");
        double bx = black / (double)(max + 1) * W, wx = (white + 1) / (double)(max + 1) * W;
        if (bx > 0) sb.Append($"<rect x=\"0\" y=\"{T}\" width=\"{bx.ToString("0.#", inv)}\" height=\"{ph}\" class=\"clip\"/>");
        if (wx < W) sb.Append($"<rect x=\"{wx.ToString("0.#", inv)}\" y=\"{T}\" width=\"{(W - wx).ToString("0.#", inv)}\" height=\"{ph}\" class=\"clip\"/>");
        for (int k = 0; k < Bins; k++)
        {
            if (bins[k] <= 0) continue;
            double hh = Math.Log10(1 + bins[k]) / top * ph;
            sb.Append($"<rect x=\"{(k * bw + 0.3).ToString("0.##", inv)}\" y=\"{(T + ph - hh).ToString("0.#", inv)}\" width=\"{(bw - 0.6).ToString("0.##", inv)}\" height=\"{hh.ToString("0.#", inv)}\" class=\"bar\"/>");
        }
        sb.Append($"<text x=\"0\" y=\"{H - 4}\">0 (deepest)</text><text x=\"{W}\" y=\"{H - 4}\" text-anchor=\"end\">{max:N0} (untouched)</text></svg>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ html bits

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Sev(Severity s) => s switch
    {
        Severity.Alert => "alert",
        Severity.Warn => "warn",
        Severity.Good => "good",
        _ => "info",
    };

    private static string Card(string title, (string, string)[] rows)
    {
        var sb = new StringBuilder($"<div class=\"card\"><h3>{E(title)}</h3><table class=\"kv\">");
        foreach (var (k, v) in rows) sb.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");
        return sb.Append("</table></div>").ToString();
    }

    private static string Fig(string src, string caption)
        => $"<figure><img src=\"{src}\" alt=\"{E(caption)}\"><figcaption>{E(caption)}</figcaption></figure>";

    private static void Row(StringBuilder sb, string k, string a, string b)
        => sb.Append($"<tr><th>{E(k)}</th><td>{E(a)}</td><td>{E(b)}</td></tr>");

    private static void KV(StringBuilder sb, string k, string v)
        => sb.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");

    private const string Css = @"
:root{--ink:#1d2229;--muted:#5d6672;--line:#d9dee5;--panel:#f5f7fa;--accent:#2f6fb3;--warn:#a86a00;--alert:#b3261e;--good:#2e7d4f}
*{box-sizing:border-box}body{margin:0;background:#fff;color:var(--ink);font:14px/1.5 'Segoe UI',system-ui,-apple-system,Helvetica,Arial,sans-serif}
main{max-width:1040px;margin:0 auto;padding:28px 24px 40px}
header{display:grid;grid-template-columns:1fr 1fr;gap:20px;align-items:start;border-bottom:2px solid var(--ink);padding-bottom:14px;margin-bottom:8px}
.kicker{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--accent);font-weight:600}
h1{font-size:24px;margin:2px 0 4px;word-break:break-all}h2{font-size:16px;margin:22px 0 8px;padding-bottom:4px;border-bottom:1px solid var(--line)}
h3{font-size:13px;margin:12px 0 6px;color:var(--muted);text-transform:uppercase;letter-spacing:.06em}
.sub{margin:0;color:var(--muted);font-size:12px}
.verdict{border-left:4px solid var(--muted);background:var(--panel);padding:8px 12px;font-size:12px}.verdict b{display:block;font-size:13px;margin-bottom:2px}
.verdict.good{border-color:var(--good)}.verdict.warn{border-color:var(--warn)}.verdict.alert{border-color:var(--alert)}
.grid3{display:grid;grid-template-columns:repeat(3,1fr);gap:14px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:8px 12px}.card h3{margin-top:2px}
table{border-collapse:collapse;width:100%}.kv th{text-align:left;font-weight:500;color:var(--muted);padding:3px 10px 3px 0;width:44%;vertical-align:top}.kv td{padding:3px 0}
.cmp th,.cmp td{padding:5px 8px;border-bottom:1px solid var(--line);text-align:right}.cmp th:first-child{text-align:left;font-weight:500;color:var(--muted)}.cmp tr:first-child th{color:var(--ink);font-weight:600}
figure{margin:0}figure img{width:100%;border:1px solid var(--line);border-radius:4px;background:#111}figcaption{font-size:11px;color:var(--muted);margin-top:3px}
.chart{width:100%;height:auto;display:block}.chart text{font-size:11px;fill:var(--muted)}.axis{stroke:var(--line)}
.surf{fill:none;stroke:var(--accent);stroke-width:1.6}.stair{fill:none;stroke:#d08b2c;stroke-width:1}.surfk{fill:var(--accent)}.stairk{fill:#d08b2c}
.bar{fill:#6f8fb4}.clip{fill:#e9d8d3}
.note{font-size:12px;color:var(--muted);margin:4px 0 10px}
.findings{padding-left:18px}.findings li{margin:4px 0}.findings .alert b{color:var(--alert)}.findings .warn b,.findings li.warn{color:var(--warn)}
.hazard{border:1px solid var(--alert);background:#fdf0ef;border-radius:6px;padding:8px 12px;margin:10px 0}.hazard b{color:var(--alert);margin-right:6px}
.hazard.caution{border-color:var(--warn);background:#fdf6e9}.hazard.caution b{color:var(--warn)}
.notes{white-space:normal;background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:10px 12px}
footer{margin-top:28px;padding-top:10px;border-top:1px solid var(--line);font-size:11px;color:var(--muted)}
@media (max-width:760px){header,.grid3{grid-template-columns:1fr}}
@media print{main{padding:0}.keep{break-inside:avoid}h2{break-after:avoid}figure img{border-color:#999}}
";
}
