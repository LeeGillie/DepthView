using System;
using System.Collections.Generic;

namespace DepthView.Processing;

/// <summary>
/// The part of the blank the job may cut, as a signed distance in pixels: positive inside the
/// engraved area, zero on its edge, negative out in the reserve (the rim or border, and any
/// hole). Everything that used to ask "how far from the centre, against the rim radius" asks
/// this instead, so a coin and a plate go through the same code - and for a coin the answer
/// is exactly the old one, radius minus distance from the centre.
///
/// Built in pixels from <see cref="TuningOptions"/> after <see cref="TuningOptions.ResolvePhysical"/>:
/// a round blank is a circle of <see cref="TuningOptions.RimRadius"/> about the rim centre; a
/// rectangular blank is the image's own rectangle inset by the border, with the corner radius
/// (less the border) and the holes (grown by the border) taken out.
/// </summary>
public sealed class ReserveRegion
{
    private readonly bool _round;
    private readonly double _cx, _cy, _radius;                    // round
    private readonly double _x0, _y0, _x1, _y1, _corner;          // rectangle: the usable box
    private readonly float[]? _sdf;                               // custom: signed distance onto the blank, pixels
    private readonly int _w, _h;
    private readonly double _border;
    private readonly List<(double X, double Y, double R)> _holes = new();

    public bool IsRound => _round;
    public bool IsCustom => _sdf is not null;
    public double CentreX => _cx;
    public double CentreY => _cy;
    /// <summary>The circle's radius for a round blank; for a rectangle or a custom outline, half its usable short side.</summary>
    public double Radius => _round ? _radius : Math.Min(_x1 - _x0, _y1 - _y0) / 2;

    private ReserveRegion(float[] sdf, int w, int h, double border)
    {
        _round = false; _sdf = sdf; _w = w; _h = h; _border = border;
        _x0 = border - 0.5; _y0 = border - 0.5; _x1 = w - 0.5 - border; _y1 = h - 0.5 - border;
        _cx = (w - 1) / 2.0; _cy = (h - 1) / 2.0;
    }

    private ReserveRegion(double cx, double cy, double radius)
    {
        _round = true; _cx = cx; _cy = cy; _radius = radius;
    }

    private ReserveRegion(double x0, double y0, double x1, double y1, double corner)
    {
        _round = false; _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; _corner = Math.Max(0, corner);
        _cx = (x0 + x1) / 2; _cy = (y0 + y1) / 2;
    }

    public static ReserveRegion Round(double cx, double cy, double radius) => new(cx, cy, radius);

    /// <summary>The region the options describe, in pixels, for an image of this size. Null when no reserve is asked for.</summary>
    public static ReserveRegion? From(TuningOptions o, int width, int height)
    {
        if (!o.AddRim) return null;
        if (o.Shape == BlankShape.Round)
            return o.RimRadius > 0 ? Round(o.RimCentreX ?? (width - 1) / 2.0, o.RimCentreY ?? (height - 1) / 2.0, o.RimRadius) : null;

        double ppmm = o.PixelsPerMm(width, height) ?? 0;
        double border = (o.RimWidthMm ?? 0) * ppmm;
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        if (o.Shape == BlankShape.Custom)
        {
            // No outline loaded: the bounding box stands in, as a plate with square corners,
            // so a missing file degrades to something visible rather than to nothing.
            if (o.Outline is null)
                return WithHoles(new ReserveRegion(border - 0.5, border - 0.5, width - 0.5 - border, height - 0.5 - border, 0), o, cx, cy, ppmm, border);
            return WithHoles(new ReserveRegion(o.Outline.SignedDistance(width, height), width, height, border), o, cx, cy, ppmm, border);
        }
        var r = new ReserveRegion(border - 0.5, border - 0.5, width - 0.5 - border, height - 0.5 - border, Math.Max(0, o.CornerRadiusMm * ppmm - border));
        return WithHoles(r, o, cx, cy, ppmm, border);
    }

    private static ReserveRegion WithHoles(ReserveRegion r, TuningOptions o, double cx, double cy, double ppmm, double border)
    {
        foreach (var h in o.Holes)
            r._holes.Add((cx + h.XMm * ppmm, cy + h.YMm * ppmm, h.DiameterMm / 2 * ppmm + border));
        return r;
    }

    /// <summary>Signed distance into the engraved area, pixels: positive inside, negative in the reserve.</summary>
    public double Inside(double x, double y)
    {
        double s;
        if (_round)
        {
            double dx = x - _cx, dy = y - _cy;
            s = _radius - Math.Sqrt(dx * dx + dy * dy);
        }
        else if (_sdf is { } sdf)
        {
            // Nearest sample of the distance field; off the field is off the blank by at
            // least the distance to its edge.
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            if (ix < 0 || iy < 0 || ix >= _w || iy >= _h)
            {
                double ox = ix < 0 ? -ix : ix >= _w ? ix - _w + 1 : 0, oy = iy < 0 ? -iy : iy >= _h ? iy - _h + 1 : 0;
                s = -Math.Sqrt(ox * ox + oy * oy) - _border;
            }
            else s = sdf[(long)iy * _w + ix] - _border;
        }
        else
        {
            // Signed distance to a rounded rectangle: inset the box by the corner radius and
            // measure to that box, then add the radius back.
            double hx = (_x1 - _x0) / 2 - _corner, hy = (_y1 - _y0) / 2 - _corner;
            double qx = Math.Abs(x - _cx) - hx, qy = Math.Abs(y - _cy) - hy;
            double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
            double insideD = Math.Min(Math.Max(qx, qy), 0);
            s = _corner - (outside + insideD);
        }
        foreach (var h in _holes)
        {
            double dx = x - h.X, dy = y - h.Y;
            s = Math.Min(s, Math.Sqrt(dx * dx + dy * dy) - h.R);
        }
        return s;
    }

    /// <summary>The usable area's bounding box, pixels.</summary>
    public (double X0, double Y0, double X1, double Y1) Bounds => _round
        ? (_cx - _radius, _cy - _radius, _cx + _radius, _cy + _radius)
        : (_x0, _y0, _x1, _y1);
}
