using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DepthView.Imaging;

namespace DepthView.Processing;

/// <summary>
/// The outline of a workpiece that is neither a circle nor a rectangle: a dog tag, a shield,
/// an oval, a pendant - drawn as an SVG, or painted as a mask image (white = the blank, black
/// = not the blank). The image being tuned spans the outline's bounding box, as it spans the
/// diameter of a coin or the width and height of a plate; the outline says which of those
/// pixels are on the blank, and the reserve is built from it the same way as from a circle:
/// as a signed distance, by a distance transform instead of arithmetic.
///
/// Loaded once per file and kept; the distance field is computed per canvas size and cached,
/// because the Tune window asks at preview size and the save asks at full size.
/// </summary>
public sealed class CustomOutline
{
    public string Path { get; }
    public string Kind { get; }                       // "svg" or "mask"
    public IReadOnlyList<string> Problems { get; }
    /// <summary>The outline's size in millimetres, when the file says (an SVG with units); else null and the blank's width and height are used.</summary>
    public double? WidthMm { get; }
    public double? HeightMm { get; }
    /// <summary>Width over height of the outline's bounding box, from the file.</summary>
    public double Aspect { get; }

    private readonly SvgOutline.Result? _svg;
    private readonly bool[]? _mask;
    private readonly int _mw, _mh;
    private readonly object _lock = new();
    private (int W, int H, float[] Sdf)? _cache;

    private CustomOutline(string path, SvgOutline.Result svg, List<string> problems)
    {
        Path = path; Kind = "svg"; _svg = svg; Problems = problems;
        WidthMm = svg.WidthMm; HeightMm = svg.HeightMm;
        Aspect = (svg.MaxX - svg.MinX) / Math.Max(1e-9, svg.MaxY - svg.MinY);
    }

    private CustomOutline(string path, bool[] mask, int w, int h, List<string> problems)
    {
        Path = path; Kind = "mask"; _mask = mask; _mw = w; _mh = h; Problems = problems;
        Aspect = w / (double)Math.Max(1, h);
    }

    private static readonly Dictionary<string, (DateTime Stamp, CustomOutline Outline)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The outline in this file, read once and kept until the file changes; null with the reason when it cannot be read.</summary>
    public static CustomOutline? Load(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "No outline file."; return null; }
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { error = ex.Message; return null; }
        if (!File.Exists(full)) { error = $"Outline file not found: {path}"; return null; }
        var stamp = File.GetLastWriteTimeUtc(full);
        lock (Cache)
        {
            if (Cache.TryGetValue(full, out var hit) && hit.Stamp == stamp) return hit.Outline;
        }
        CustomOutline? o;
        string? why = null;
        try { o = Read(full, out why); }
        catch (Exception ex) { error = $"Could not read {System.IO.Path.GetFileName(full)}: {ex.Message}"; return null; }
        if (o is null) { error = $"{System.IO.Path.GetFileName(full)} holds no outline." + (why is null ? "" : " " + why); return null; }
        lock (Cache) Cache[full] = (stamp, o);
        return o;
    }

    private static CustomOutline? Read(string full, out string? why)
    {
        why = null;
        string ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        if (ext == ".svg")
        {
            var svg = SvgOutline.Parse(File.ReadAllText(full));
            if (svg.Polygons.Count == 0) { why = string.Join(" ", svg.Problems); return null; }
            return new CustomOutline(full, svg, svg.Problems);
        }
        // Anything else is read as an image: white is the blank.
        var (img, _) = ImageLoader.Load(File.ReadAllBytes(full), System.IO.Path.GetFileName(full), full, "outline");
        var mask = new bool[(long)img.Width * img.Height];
        int ch = img.Channels;
        var problems = new List<string>();
        if (img.Samples is { } s)
        {
            double half = img.MaxValue / 2.0;
            for (long i = 0; i < mask.Length; i++) mask[i] = s[i * ch] > half;
        }
        else if (img.Floats is { } f)
        {
            for (long i = 0; i < mask.Length; i++) mask[i] = f[i * ch] > 0.5f;
        }
        else return null;
        long on = mask.Count(b => b);
        if (on == 0) { problems.Add("The mask is all black: nothing is on the blank."); return null; }
        if (on == mask.Length) problems.Add("The mask is all white: the whole image is the blank, like a plate with no corners.");
        return new CustomOutline(full, mask, img.Width, img.Height, problems);
    }

    /// <summary>Which pixels of a w × h map are on the blank: the outline scaled onto the map.</summary>
    public bool[] Rasterise(int w, int h)
    {
        if (_svg is { } svg) return SvgOutline.Rasterise(svg.Polygons, svg.MinX, svg.MinY, svg.MaxX, svg.MaxY, w, h);
        var mask = new bool[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            int my = Math.Min(_mh - 1, (int)((y + 0.5) * _mh / h));
            long row = (long)y * w, mrow = (long)my * _mw;
            for (int x = 0; x < w; x++)
            {
                int mx = Math.Min(_mw - 1, (int)((x + 0.5) * _mw / w));
                mask[row + x] = _mask![mrow + mx];
            }
        }
        return mask;
    }

    /// <summary>Signed distance into the blank, pixels, for a map of this size: positive on the blank, negative off it. Cached per size.</summary>
    public float[] SignedDistance(int w, int h)
    {
        lock (_lock)
        {
            if (_cache is { } c && c.W == w && c.H == h) return c.Sdf;
            var sdf = DistanceTransform.Signed(Rasterise(w, h), w, h);
            _cache = (w, h, sdf);
            return sdf;
        }
    }

    /// <summary>
    /// The outline's edge as closed polylines in pixels of a w × h map, traced from the distance
    /// field. The field is traced with one ring of "outside" around it, so an outline that runs
    /// to the edge of its bounding box - every straight-sided tag - closes along that edge
    /// instead of breaking into the corner arcs.
    /// </summary>
    public List<List<Pt>> EdgePx(int w, int h, double insetPx = 0)
    {
        var sdf = SignedDistance(w, h);
        int pw = w + 2, ph = h + 2;
        var padded = new float[(long)pw * ph];
        Array.Fill(padded, -0.5f);
        for (int y = 0; y < h; y++) Array.Copy(sdf, (long)y * w, padded, (long)(y + 1) * pw + 1, w);
        // An edge pixel on the blank is +0.5 (the distance transform counts the canvas edge as
        // outside), the ring is -0.5, so the zero contour crosses exactly on the canvas edge.
        var loops = MarchingSquares.Contours(padded, pw, ph, (float)insetPx);
        foreach (var loop in loops)
            for (int i = 0; i < loop.Count; i++) loop[i] = new Pt(loop[i].X - 1, loop[i].Y - 1);
        return loops;
    }
}
