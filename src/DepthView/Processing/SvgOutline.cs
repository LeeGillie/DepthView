using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace DepthView.Processing;

/// <summary>A point in an outline, user units or pixels.</summary>
public readonly record struct Pt(double X, double Y);

/// <summary>
/// A small SVG reader: enough of the format to take an outline drawn in Inkscape, LightBurn,
/// Illustrator or by hand - paths, rects, circles, ellipses, polygons - and turn it into
/// closed polygons, with transforms applied and curves flattened. Everything is filled with
/// the even-odd rule, so a hole drawn as an inner contour is a hole. What it does not read
/// (text, images, strokes as areas, clip paths, use/symbol) is listed in the problems so the
/// user knows what was left out, rather than silently drawn as nothing.
/// </summary>
public static class SvgOutline
{
    public sealed class Result
    {
        public List<List<Pt>> Polygons = new();
        /// <summary>The drawing's size in millimetres, when width/height carry a unit that can be converted.</summary>
        public double? WidthMm, HeightMm;
        /// <summary>Bounding box of the polygons, user units.</summary>
        public double MinX, MinY, MaxX, MaxY;
        public List<string> Problems = new();
    }

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    public static Result Parse(string text)
    {
        var r = new Result();
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (Exception ex) { r.Problems.Add("Not readable as SVG: " + ex.Message); return r; }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "svg") { r.Problems.Add("The file's root element is not <svg>."); return r; }

        // Physical size, when the file says. A viewBox maps user units onto that size; the
        // polygons stay in user units and the caller scales by the bounding box.
        double? wMm = Length(root.Attribute("width")?.Value), hMm = Length(root.Attribute("height")?.Value);
        double[]? vb = root.Attribute("viewBox")?.Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, C)).ToArray();
        if (vb is { Length: 4 } && wMm is { } wm && hMm is { } hm && vb[2] > 0 && vb[3] > 0)
        {
            // Millimetres per user unit, so the polygons' bounding box can be sized.
            r.WidthMm = wm; r.HeightMm = hm;
            _mmPerUnitX = wm / vb[2]; _mmPerUnitY = hm / vb[3];
        }
        else { _mmPerUnitX = _mmPerUnitY = null; r.WidthMm = wMm; r.HeightMm = hMm; }

        Walk(root, Affine.Identity, r, 0);
        if (r.Polygons.Count == 0) { r.Problems.Add("No filled shape was found (paths, rects, circles, ellipses, polygons)."); return r; }

        r.MinX = r.MinY = double.PositiveInfinity; r.MaxX = r.MaxY = double.NegativeInfinity;
        foreach (var poly in r.Polygons)
            foreach (var p in poly)
            {
                if (p.X < r.MinX) r.MinX = p.X; if (p.X > r.MaxX) r.MaxX = p.X;
                if (p.Y < r.MinY) r.MinY = p.Y; if (p.Y > r.MaxY) r.MaxY = p.Y;
            }
        // The outline's own size in mm comes from the bounding box when the file carries units.
        if (_mmPerUnitX is { } mx && _mmPerUnitY is { } my)
        {
            r.WidthMm = (r.MaxX - r.MinX) * mx;
            r.HeightMm = (r.MaxY - r.MinY) * my;
        }
        return r;
    }

    [ThreadStatic] private static double? _mmPerUnitX, _mmPerUnitY;

    /// <summary>A length with an SVG unit, in millimetres; null when there is no unit worth trusting (px, %, none).</summary>
    private static double? Length(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string unit = new string(s.Reverse().TakeWhile(ch => char.IsLetter(ch) || ch == '%').Reverse().ToArray());
        string num = s.Substring(0, s.Length - unit.Length);
        if (!double.TryParse(num, NumberStyles.Float, C, out double v)) return null;
        return unit.ToLowerInvariant() switch
        {
            "mm" => v, "cm" => v * 10, "in" => v * 25.4, "pt" => v * 25.4 / 72, "pc" => v * 25.4 / 6,
            _ => null,
        };
    }

    private readonly record struct Affine(double A, double B, double Cc, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);
        public Pt Apply(Pt p) => new(A * p.X + Cc * p.Y + E, B * p.X + D * p.Y + F);
        /// <summary>this ∘ other: other is applied first.</summary>
        public Affine Then(Affine o) => new(
            A * o.A + Cc * o.B, B * o.A + D * o.B,
            A * o.Cc + Cc * o.D, B * o.Cc + D * o.D,
            A * o.E + Cc * o.F + E, B * o.E + D * o.F + F);
    }

    private static void Walk(XElement el, Affine t, Result r, int depth)
    {
        if (depth > 64) return;
        string name = el.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask" or "marker" or "symbol" or "pattern" or "metadata" or "title" or "desc" or "style" or "namedview") return;
        if (Attr(el, "display") == "none") return;
        var local = ParseTransform(Attr(el, "transform"), r);
        var here = t.Then(local);
        switch (name)
        {
            case "svg" or "g" or "a" or "switch":
                foreach (var child in el.Elements()) Walk(child, here, r, depth + 1);
                return;
            case "path": AddPolys(r, PathPolys(Attr(el, "d") ?? "", r), here); return;
            case "rect": AddPolys(r, RectPolys(el), here); return;
            case "circle": AddPolys(r, EllipsePolys(Num(el, "cx"), Num(el, "cy"), Num(el, "r"), Num(el, "r")), here); return;
            case "ellipse": AddPolys(r, EllipsePolys(Num(el, "cx"), Num(el, "cy"), Num(el, "rx"), Num(el, "ry")), here); return;
            case "polygon" or "polyline": AddPolys(r, new[] { Points(Attr(el, "points") ?? "") }, here); return;
            case "line": return;   // no area
            case "text" or "image" or "use":
                if (!r.Problems.Any(p => p.StartsWith("<" + name))) r.Problems.Add($"<{name}> is not read; convert it to a path first.");
                return;
            default: return;
        }
    }

    private static void AddPolys(Result r, IEnumerable<List<Pt>> polys, Affine t)
    {
        foreach (var poly in polys)
        {
            if (poly.Count < 3) continue;
            var outp = new List<Pt>(poly.Count);
            foreach (var p in poly) outp.Add(t.Apply(p));
            r.Polygons.Add(outp);
        }
    }

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;
    private static double Num(XElement el, string name) => double.TryParse(Attr(el, name), NumberStyles.Float, C, out double v) ? v : 0;

    private static Affine ParseTransform(string? s, Result r)
    {
        var t = Affine.Identity;
        if (string.IsNullOrWhiteSpace(s)) return t;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            int start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            string fn = s.Substring(start, i - start);
            if (fn.Length == 0) break;
            int open = s.IndexOf('(', i); if (open < 0) break;
            int close = s.IndexOf(')', open); if (close < 0) break;
            var a = s.Substring(open + 1, close - open - 1).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, C)).ToArray();
            i = close + 1;
            Affine m = fn switch
            {
                "translate" => new(1, 0, 0, 1, a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0),
                "scale" => new(a.Length > 0 ? a[0] : 1, 0, 0, a.Length > 1 ? a[1] : a.Length > 0 ? a[0] : 1, 0, 0),
                "rotate" => Rotate(a),
                "matrix" when a.Length == 6 => new(a[0], a[1], a[2], a[3], a[4], a[5]),
                "skewX" => new(1, 0, Math.Tan((a.Length > 0 ? a[0] : 0) * Math.PI / 180), 1, 0, 0),
                "skewY" => new(1, Math.Tan((a.Length > 0 ? a[0] : 0) * Math.PI / 180), 0, 1, 0, 0),
                _ => Affine.Identity,
            };
            t = t.Then(m);
        }
        return t;
    }

    private static Affine Rotate(double[] a)
    {
        double ang = (a.Length > 0 ? a[0] : 0) * Math.PI / 180, cs = Math.Cos(ang), sn = Math.Sin(ang);
        var rot = new Affine(cs, sn, -sn, cs, 0, 0);
        if (a.Length < 3) return rot;
        return new Affine(1, 0, 0, 1, a[1], a[2]).Then(rot).Then(new Affine(1, 0, 0, 1, -a[1], -a[2]));
    }

    private static IEnumerable<List<Pt>> RectPolys(XElement el)
    {
        double x = Num(el, "x"), y = Num(el, "y"), w = Num(el, "width"), h = Num(el, "height");
        double rx = Num(el, "rx"), ry = Num(el, "ry");
        if (rx > 0 && ry <= 0) ry = rx; if (ry > 0 && rx <= 0) rx = ry;
        rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
        if (w <= 0 || h <= 0) yield break;
        if (rx <= 0 || ry <= 0) { yield return new List<Pt> { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) }; yield break; }
        var poly = new List<Pt>();
        void Arc(double cx, double cy, double a0, double a1)
        {
            const int n = 12;
            for (int k = 0; k <= n; k++) { double a = a0 + (a1 - a0) * k / n; poly.Add(new(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a))); }
        }
        Arc(x + w - rx, y + ry, -Math.PI / 2, 0);
        Arc(x + w - rx, y + h - ry, 0, Math.PI / 2);
        Arc(x + rx, y + h - ry, Math.PI / 2, Math.PI);
        Arc(x + rx, y + ry, Math.PI, 3 * Math.PI / 2);
        yield return poly;
    }

    private static IEnumerable<List<Pt>> EllipsePolys(double cx, double cy, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0) yield break;
        int n = Math.Clamp((int)(Math.Max(rx, ry) * 2), 48, 720);
        var poly = new List<Pt>(n);
        for (int k = 0; k < n; k++) { double a = 2 * Math.PI * k / n; poly.Add(new(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a))); }
        yield return poly;
    }

    private static List<Pt> Points(string s)
    {
        var nums = s.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, C)).ToArray();
        var poly = new List<Pt>(nums.Length / 2);
        for (int k = 0; k + 1 < nums.Length; k += 2) poly.Add(new(nums[k], nums[k + 1]));
        return poly;
    }

    // ------------------------------------------------------------------ path data

    /// <summary>
    /// Path data to closed polygons: one per subpath. Curves are flattened to short segments
    /// (16 per Bézier, 8 per arc quadrant); an open subpath is closed, since only areas mean
    /// anything here.
    /// </summary>
    private static List<List<Pt>> PathPolys(string d, Result r)
    {
        var polys = new List<List<Pt>>();
        List<object> tokens;
        try { tokens = Tokenize(d); }
        catch (FormatException ex) { r.Problems.Add("Path data: " + ex.Message); return polys; }
        int i = 0;
        char cmd = ' ';
        Pt cur = new(0, 0), start = new(0, 0), lastCtrl = new(0, 0);
        char lastCmd = ' ';
        List<Pt>? poly = null;

        double Next()
        {
            if (i < tokens.Count && tokens[i] is double v) { i++; return v; }
            throw new FormatException("path data ended early");
        }
        bool MoreNumbers() => i < tokens.Count && tokens[i] is double;
        void Close() { if (poly is { Count: >= 3 }) polys.Add(poly); poly = null; }
        void Line(Pt p) { poly ??= new List<Pt> { cur }; poly.Add(p); cur = p; }

        try
        {
            while (i < tokens.Count)
            {
                if (tokens[i] is char c) { cmd = c; i++; }
                else if (cmd == ' ') break;
                bool rel = char.IsLower(cmd);
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        Close();
                        double x = Next(), y = Next();
                        cur = rel ? new(cur.X + x, cur.Y + y) : new(x, y);
                        start = cur;
                        poly = new List<Pt> { cur };
                        // Further pairs after M are implicit L.
                        while (MoreNumbers()) { double lx = Next(), ly = Next(); Line(rel ? new(cur.X + lx, cur.Y + ly) : new(lx, ly)); }
                        break;
                    }
                    case 'L': while (MoreNumbers()) { double x = Next(), y = Next(); Line(rel ? new(cur.X + x, cur.Y + y) : new(x, y)); } break;
                    case 'H': while (MoreNumbers()) { double x = Next(); Line(rel ? new(cur.X + x, cur.Y) : new(x, cur.Y)); } break;
                    case 'V': while (MoreNumbers()) { double y = Next(); Line(rel ? new(cur.X, cur.Y + y) : new(cur.X, y)); } break;
                    case 'C':
                        while (MoreNumbers())
                        {
                            Pt c1 = Abs(rel, cur, Next(), Next()), c2 = Abs(rel, cur, Next(), Next()), p = Abs(rel, cur, Next(), Next());
                            Cubic(cur, c1, c2, p, Line); lastCtrl = c2;
                        }
                        break;
                    case 'S':
                        while (MoreNumbers())
                        {
                            Pt c1 = char.ToUpperInvariant(lastCmd) is 'C' or 'S' ? new(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                            Pt c2 = Abs(rel, cur, Next(), Next()), p = Abs(rel, cur, Next(), Next());
                            Cubic(cur, c1, c2, p, Line); lastCtrl = c2;
                        }
                        break;
                    case 'Q':
                        while (MoreNumbers())
                        {
                            Pt c1 = Abs(rel, cur, Next(), Next()), p = Abs(rel, cur, Next(), Next());
                            Quad(cur, c1, p, Line); lastCtrl = c1;
                        }
                        break;
                    case 'T':
                        while (MoreNumbers())
                        {
                            Pt c1 = char.ToUpperInvariant(lastCmd) is 'Q' or 'T' ? new(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                            Pt p = Abs(rel, cur, Next(), Next());
                            Quad(cur, c1, p, Line); lastCtrl = c1;
                        }
                        break;
                    case 'A':
                        while (MoreNumbers())
                        {
                            double rx = Next(), ry = Next(), rot = Next(); bool large = Next() != 0, sweep = Next() != 0;
                            Pt p = Abs(rel, cur, Next(), Next());
                            Arc(cur, rx, ry, rot, large, sweep, p, Line);
                        }
                        break;
                    case 'Z':
                        Line(start);
                        Close();
                        cur = start;
                        break;
                    default:
                        r.Problems.Add($"Path command '{cmd}' is not understood; the rest of that path was skipped.");
                        i = tokens.Count;
                        break;
                }
                lastCmd = cmd;
            }
        }
        catch (FormatException ex) { r.Problems.Add("Path data: " + ex.Message); }
        Close();
        return polys;
    }

    private static Pt Abs(bool rel, Pt cur, double x, double y) => rel ? new(cur.X + x, cur.Y + y) : new(x, y);

    private static List<object> Tokenize(string d)
    {
        var list = new List<object>();
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c) && c != 'e' && c != 'E') { list.Add(c); i++; continue; }
            int start = i;
            if (c == '-' || c == '+') i++;
            bool dot = false, exp = false;
            while (i < d.Length)
            {
                char ch = d[i];
                if (char.IsDigit(ch)) { i++; continue; }
                if (ch == '.' && !dot && !exp) { dot = true; i++; continue; }
                if ((ch == 'e' || ch == 'E') && !exp) { exp = true; i++; if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++; continue; }
                break;
            }
            if (i == start) throw new FormatException($"unexpected '{c}' in path data");
            list.Add(double.Parse(d.AsSpan(start, i - start), NumberStyles.Float, C));
            // Flags of an arc are single digits that may be run together: handled by the arc
            // reader taking whole numbers; "01" style flags are rare and read as 1 here.
        }
        return list;
    }

    private static void Cubic(Pt p0, Pt p1, Pt p2, Pt p3, Action<Pt> emit)
    {
        const int n = 16;
        for (int k = 1; k <= n; k++)
        {
            double t = k / (double)n, u = 1 - t;
            emit(new(u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                     u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    private static void Quad(Pt p0, Pt p1, Pt p2, Action<Pt> emit)
    {
        const int n = 12;
        for (int k = 1; k <= n; k++)
        {
            double t = k / (double)n, u = 1 - t;
            emit(new(u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y));
        }
    }

    /// <summary>SVG arc to segments, by the implementation notes' endpoint-to-centre conversion.</summary>
    private static void Arc(Pt p0, double rx, double ry, double rotDeg, bool large, bool sweep, Pt p1, Action<Pt> emit)
    {
        if (rx == 0 || ry == 0 || (p0.X == p1.X && p0.Y == p1.Y)) { emit(p1); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = rotDeg * Math.PI / 180, cs = Math.Cos(phi), sn = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
        double x1 = cs * dx + sn * dy, y1 = -sn * dx + cs * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
        double cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
        double cx = cs * cx1 - sn * cy1 + (p0.X + p1.X) / 2, cy = sn * cx1 + cs * cy1 + (p0.Y + p1.Y) / 2;
        double a0 = Math.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
        double a1 = Math.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
        double da = a1 - a0;
        if (sweep && da < 0) da += 2 * Math.PI;
        if (!sweep && da > 0) da -= 2 * Math.PI;
        int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(da) / (Math.PI / 2) * 8));
        for (int k = 1; k <= n; k++)
        {
            double a = a0 + da * k / n, ex = rx * Math.Cos(a), ey = ry * Math.Sin(a);
            emit(new(cs * ex - sn * ey + cx, sn * ex + cs * ey + cy));
        }
    }

    // ------------------------------------------------------------------ rasterising

    /// <summary>
    /// Fill the polygons into a mask of this size, even-odd. The polygons' bounding box maps
    /// onto the whole mask, since the image spans the blank's bounding box.
    /// </summary>
    public static bool[] Rasterise(IReadOnlyList<List<Pt>> polys, double minX, double minY, double maxX, double maxY, int w, int h)
    {
        var mask = new bool[(long)w * h];
        double sx = w / Math.Max(1e-9, maxX - minX), sy = h / Math.Max(1e-9, maxY - minY);
        var xs = new List<double>();
        for (int y = 0; y < h; y++)
        {
            double yy = (y + 0.5) / sy + minY;
            xs.Clear();
            foreach (var poly in polys)
            {
                for (int k = 0; k < poly.Count; k++)
                {
                    Pt a = poly[k], b = poly[(k + 1) % poly.Count];
                    if ((a.Y <= yy) == (b.Y <= yy)) continue;
                    double x = a.X + (yy - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    xs.Add((x - minX) * sx);
                }
            }
            if (xs.Count < 2) continue;
            xs.Sort();
            long row = (long)y * w;
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)Math.Round(xs[k])), x1 = Math.Min(w, (int)Math.Round(xs[k + 1]));
                for (int x = x0; x < x1; x++) mask[row + x] = true;
            }
        }
        return mask;
    }
}
