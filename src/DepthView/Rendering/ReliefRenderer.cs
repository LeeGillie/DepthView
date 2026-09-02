using System;
using System.Threading.Tasks;
using DepthView.Imaging;

namespace DepthView.Rendering;

public sealed class ReliefOptions
{
    public MaterialPreset Material = MaterialPreset.Builtins()[0];

    /// <summary>Compass bearing of the light, 0 = from the top of the image, clockwise.</summary>
    public double LightAzimuthDeg = 315;
    public double LightElevationDeg = 42;

    /// <summary>Vertical exaggeration. Laser relief is very shallow, so 1.0 is already boosted.</summary>
    public double Exaggeration = 1.0;

    public double AoStrength = 1.0;
    public double Zoom = 1.0;
    public double PanX, PanY;
    public bool InvertHeight;

    /// <summary>0 = continuous. Above 0, quantise to this many steps to preview slice terracing.</summary>
    public int SliceCount;

    // --- camera ---------------------------------------------------------
    /// <summary>False renders the fast, pixel-exact top-down view.</summary>
    public bool Orbit;

    /// <summary>Camera bearing around the piece, degrees.</summary>
    public double YawDeg;

    /// <summary>Camera elevation, degrees. 90 looks straight down.</summary>
    public double PitchDeg = 62;

    /// <summary>Vertices across the mesh. Only affects silhouette accuracy, not shading.</summary>
    public int MeshResolution = 384;

    /// <summary>1 = full quality, 2 = half resolution sampling for interactive dragging.</summary>
    public int Quality = 1;

    /// <summary>Supersampling factor for the orbit path, to keep silhouettes from stair-stepping.</summary>
    public int Supersample = 1;
}

/// <summary>
/// Software renderer for a lit height field, with an optional orbiting camera.
/// Deliberately no GPU: OpenGL is the first thing to fail over a remote desktop session,
/// which is exactly where the machine driving the laser tends to live.
///
/// The two paths share their shading. Geometry is the only thing the mesh is responsible
/// for - silhouette and occlusion - while normals, textures and occlusion are all sampled
/// per pixel from the height field itself. That is why a coarse mesh still shades crisply:
/// tessellation density costs you edge accuracy, not surface detail.
/// </summary>
public static class ReliefRenderer
{
    // Kept close to neutral on purpose: a blue-white environment fights a warm metal's own
    // tint and drags brass toward olive, because the environment colour multiplies the
    // specular tint rather than sitting alongside it.
    private const double SkyR = 0.890, SkyG = 0.885, SkyB = 0.900;
    private const double FloorR = 0.032, FloorG = 0.033, FloorB = 0.038;
    private const double BackR = 0.055, BackG = 0.062, BackB = 0.072;

    // ------------------------------------------------------------------ height field

    /// <summary>
    /// Builds a normalised 0..1 height field from a plain grey buffer.
    ///
    /// This is the path the tuning dialog uses. It already holds a corrected buffer at preview
    /// resolution - the whole pipeline, levels through rim, has been run on it - so handing that
    /// straight to the renderer is both cheaper and more honest than re-deriving a surface from
    /// something else. What you see lit is the buffer that would be written.
    ///
    /// Point sampling, not box averaging, and this one matters more than it looks. Shading is
    /// driven by the slope between neighbouring samples, so averaging a 3 x 3 block turns the
    /// one-pixel riser of a terrace into a three-pixel ramp - and a ramp catches the light like
    /// a smooth surface. Averaging therefore erases exactly the staircase this view exists to
    /// show, and erases it most thoroughly at low pass counts where the terracing matters most.
    /// The flat preview reduces nearest-neighbour for the same underlying reason: a resampler
    /// that invents intermediate values is inventing depths the file does not contain.
    /// </summary>
    public static float[] BuildHeights(ushort[] grey, int w, int h, int maxValue,
                                       int maxEdge, out int outW, out int outH)
    {
        int step = 1;
        while (Math.Max(w, h) / step > maxEdge) step++;

        outW = Math.Max(1, w / step);
        outH = Math.Max(1, h / step);

        var result = new float[(long)outW * outH];
        double span = maxValue <= 0 ? 1 : maxValue;

        for (int y = 0; y < outH; y++)
        {
            int sy = Math.Min(h - 1, y * step);
            long srow = (long)sy * w;
            long drow = (long)y * outW;
            for (int x = 0; x < outW; x++)
            {
                int sx = Math.Min(w - 1, x * step);
                result[drow + x] = (float)Math.Clamp(grey[srow + sx] / span, 0.0, 1.0);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a normalised 0..1 height field from decoded image data, box-averaging down to
    /// <paramref name="maxEdge"/> so very large maps stay interactive.
    /// </summary>
    public static float[] BuildHeights(ImageData img, int maxEdge, out int outW, out int outH)
    {
        int step = 1;
        while (Math.Max(img.Width, img.Height) / step > maxEdge) step++;

        outW = Math.Max(1, img.Width / step);
        outH = Math.Max(1, img.Height / step);

        var result = new float[(long)outW * outH];
        int ch = img.Channels;

        float lo, hi;
        if (img.Kind == SampleKind.Float)
        {
            lo = float.PositiveInfinity; hi = float.NegativeInfinity;
            var fs = img.Floats!;
            for (long i = 0; i < fs.LongLength; i += ch)
            {
                float v = fs[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (float.IsInfinity(lo)) { lo = 0; hi = 1; }
        }
        else { lo = 0; hi = img.MaxValue; }

        double span = hi - lo;
        if (span <= 0) span = 1;

        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                double sum = 0;
                int n = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int sy = y * step + dy;
                    if (sy >= img.Height) break;
                    for (int dx = 0; dx < step; dx++)
                    {
                        int sx = x * step + dx;
                        if (sx >= img.Width) break;
                        long o = ((long)sy * img.Width + sx) * ch;

                        double v;
                        if (img.Kind == SampleKind.Float) v = img.Floats![o];
                        else if (ch >= 3) v = (img.Samples![o] + img.Samples[o + 1] + img.Samples[o + 2]) / 3.0;
                        else v = img.Samples![o];

                        if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                        sum += v;
                        n++;
                    }
                }
                result[(long)y * outW + x] = n == 0 ? 0f : (float)Math.Clamp((sum / n - lo) / span, 0, 1);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ entry point

    public static void Render(byte[] bgra, int outW, int outH, ReliefScene scene, ReliefOptions o)
    {
        var m = o.Material;
        m.Resolve();

        double zk = scene.W / 8.0 * Math.Max(0.001, o.Exaggeration);
        var ao = scene.Occlusion(zk, o.AoStrength, o.SliceCount, o.InvertHeight);

        // Camera direction, from the surface toward the viewer.
        double pel = o.PitchDeg * Math.PI / 180.0;
        double paz = o.YawDeg * Math.PI / 180.0;
        double cx = Math.Cos(pel) * Math.Sin(paz);
        double cy = -Math.Cos(pel) * Math.Cos(paz);
        double cz = Math.Sin(pel);

        var sh = new Shader(scene, o, zk, ao);

        if (!o.Orbit)
        {
            sh.SetView(0, 0, 1);
            RenderFlat(bgra, outW, outH, scene, o, sh);
        }
        else
        {
            sh.SetView(cx, cy, cz);
            RenderOrbit(bgra, outW, outH, scene, o, sh, cx, cy, cz, zk);
        }
    }

    // ------------------------------------------------------------------ flat path

    private static void RenderFlat(byte[] bgra, int outW, int outH,
                                   ReliefScene scene, ReliefOptions o, Shader sh)
    {
        int fw = scene.W, fh = scene.H;
        double halfW = outW * 0.5, halfH = outH * 0.5;
        double inv = 1.0 / Math.Max(0.01, o.Zoom);

        Parallel.For(0, outH, y =>
        {
            double sy0 = (y - halfH) * inv + o.PanY + fh * 0.5;
            int rowBase = y * outW * 4;

            for (int x = 0; x < outW; x++)
            {
                double sx0 = (x - halfW) * inv + o.PanX + fw * 0.5;
                int d = rowBase + x * 4;

                if (sx0 < 0 || sy0 < 0 || sx0 > fw - 1 || sy0 > fh - 1)
                {
                    Write(bgra, d, BackR, BackG, BackB);
                    continue;
                }

                sh.Shade(sx0, sy0, out double r, out double g, out double b);
                Write(bgra, d, r, g, b);
            }
        });
    }

    // ------------------------------------------------------------------ orbit path

    private static void RenderOrbit(byte[] bgra, int outW, int outH,
                                    ReliefScene scene, ReliefOptions o, Shader sh,
                                    double camX, double camY, double camZ, double zk)
    {
        int ss = Math.Clamp(o.Supersample, 1, 3);
        if ((long)outW * outH * ss * ss > 6_000_000) ss = 1;

        int iw = outW * ss, ih = outH * ss;
        byte[] target = ss == 1 ? bgra : new byte[(long)iw * ih * 4];

        // Camera basis. The world has +x right, +y toward the top of the image, +z out of
        // the plate, so at pitch 90 this reproduces the flat view exactly.
        double fx = -camX, fy = -camY, fz = -camZ;
        double ux = 0, uy = 0, uz = 1;
        if (Math.Abs(fx * ux + fy * uy + fz * uz) > 0.999) { ux = 0; uy = 1; uz = 0; }

        double rx = fy * uz - fz * uy;
        double ry = fz * ux - fx * uz;
        double rz = fx * uy - fy * ux;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        rx /= rl; ry /= rl; rz /= rl;

        double vx = ry * fz - rz * fy;
        double vy = rz * fx - rx * fz;
        double vz = rx * fy - ry * fx;

        int fwi = scene.W, fhi = scene.H;
        int gw = Math.Clamp(o.MeshResolution, 2, Math.Max(2, fwi));
        int gh = Math.Max(2, (int)Math.Round(gw * (double)(fhi - 1) / Math.Max(1, fwi - 1)));

        // Main grid, then a ring of base vertices used to give the plate a visible edge.
        int grid = gw * gh;
        int ring = 2 * gw + 2 * gh;
        int vcount = grid + ring;

        var vsx = new float[vcount];
        var vsy = new float[vcount];
        var vdp = new float[vcount];
        var vfx = new float[vcount];
        var vfy = new float[vcount];
        var vwx = new float[vcount];
        var vwy = new float[vcount];
        var vwz = new float[vcount];

        double zoom = Math.Max(0.01, o.Zoom) * ss;
        double ox = iw * 0.5, oy = ih * 0.5;
        double stepX = (fwi - 1) / (double)(gw - 1);
        double stepY = (fhi - 1) / (double)(gh - 1);

        Parallel.For(0, gh, gy =>
        {
            double ffy = gy * stepY;
            double wy = fhi * 0.5 - ffy;
            int row = gy * gw;

            for (int gx = 0; gx < gw; gx++)
            {
                double ffx = gx * stepX;
                double wx = ffx - fwi * 0.5;
                double wz = sh.Height(ffx, ffy) * zk;

                vsx[row + gx] = (float)((wx * rx + wy * ry + wz * rz - o.PanX) * zoom + ox);
                vsy[row + gx] = (float)((-(wx * vx + wy * vy + wz * vz) - o.PanY) * zoom + oy);
                vdp[row + gx] = (float)(wx * camX + wy * camY + wz * camZ);
                vfx[row + gx] = (float)ffx;
                vfy[row + gx] = (float)ffy;
                vwx[row + gx] = (float)wx;
                vwy[row + gx] = (float)wy;
                vwz[row + gx] = (float)wz;
            }
        });

        // Skirt: without it the workpiece is a sheet of paper at any grazing angle.
        double baseZ = -Math.Max(zk * 0.06, fwi * 0.012);

        void Base(int slot, int src)
        {
            double wx = vwx[src], wy = vwy[src];
            vsx[grid + slot] = (float)((wx * rx + wy * ry + baseZ * rz - o.PanX) * zoom + ox);
            vsy[grid + slot] = (float)((-(wx * vx + wy * vy + baseZ * vz) - o.PanY) * zoom + oy);
            vdp[grid + slot] = (float)(wx * camX + wy * camY + baseZ * camZ);
            vfx[grid + slot] = vfx[src];
            vfy[grid + slot] = vfy[src];
            vwx[grid + slot] = (float)wx;
            vwy[grid + slot] = (float)wy;
            vwz[grid + slot] = (float)baseZ;
        }

        for (int gx = 0; gx < gw; gx++)
        {
            Base(gx, gx);                                   // top edge
            Base(gw + gx, (gh - 1) * gw + gx);              // bottom edge
        }
        for (int gy = 0; gy < gh; gy++)
        {
            Base(2 * gw + gy, gy * gw);                     // left edge
            Base(2 * gw + gh + gy, gy * gw + gw - 1);       // right edge
        }

        var depth = new float[(long)iw * ih];
        Array.Fill(depth, float.NegativeInfinity);

        // Fill the background once from precomputed bytes. Calling the full Write path here
        // would run a pow() per pixel for a colour that never varies.
        Write(target, 0, BackR, BackG, BackB);
        byte b0 = target[0], b1 = target[1], b2 = target[2];
        for (long i = 0, n = (long)iw * ih * 4; i < n; i += 4)
        {
            target[i] = b0;
            target[i + 1] = b1;
            target[i + 2] = b2;
            target[i + 3] = 255;
        }

        int bands = Math.Clamp(Environment.ProcessorCount, 1, 32);

        Parallel.For(0, bands, band =>
        {
            int y0 = (int)((long)band * ih / bands);
            int y1 = (int)((long)(band + 1) * ih / bands);
            if (y1 <= y0) return;

            for (int gy = 0; gy < gh - 1; gy++)
            {
                int r0 = gy * gw, r1 = (gy + 1) * gw;

                for (int gx = 0; gx < gw - 1; gx++)
                {
                    int a = r0 + gx, b = r0 + gx + 1, c = r1 + gx + 1, e = r1 + gx;
                    Triangle(target, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz, a, b, c);
                    Triangle(target, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz, a, c, e);
                }
            }

            for (int gx = 0; gx < gw - 1; gx++)
            {
                Quad(target, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz,
                     gx, gx + 1, grid + gx + 1, grid + gx);

                int bl = (gh - 1) * gw;
                Quad(target, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz,
                     bl + gx + 1, bl + gx, grid + gw + gx, grid + gw + gx + 1);
            }

            for (int gy = 0; gy < gh - 1; gy++)
            {
                Quad(target, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz,
                     (gy + 1) * gw, gy * gw, grid + 2 * gw + gy, grid + 2 * gw + gy + 1);

                int rr = gw - 1;
                Quad(target, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz,
                     gy * gw + rr, (gy + 1) * gw + rr, grid + 2 * gw + gh + gy + 1, grid + 2 * gw + gh + gy);
            }
        });

        if (ss != 1) Downsample(target, bgra, outW, outH, ss);
    }

    private static void Quad(byte[] buf, float[] depth, int iw, int y0, int y1, Shader sh,
                             float[] vsx, float[] vsy, float[] vdp, float[] vfx, float[] vfy,
                             float[] vwx, float[] vwy, float[] vwz,
                             int a, int b, int c, int d)
    {
        Triangle(buf, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz, a, b, c);
        Triangle(buf, depth, iw, y0, y1, sh, vsx, vsy, vdp, vfx, vfy, vwx, vwy, vwz, a, c, d);
    }

    private static void Triangle(byte[] buf, float[] depth, int iw, int y0, int y1, Shader sh,
                                 float[] vsx, float[] vsy, float[] vdp, float[] vfx, float[] vfy,
                                 float[] vwx, float[] vwy, float[] vwz,
                                 int i0, int i1, int i2)
    {
        double x0 = vsx[i0], y0d = vsy[i0];
        double x1 = vsx[i1], y1d = vsy[i1];
        double x2 = vsx[i2], y2d = vsy[i2];

        double minY = Math.Min(y0d, Math.Min(y1d, y2d));
        double maxY = Math.Max(y0d, Math.Max(y1d, y2d));
        if (maxY < y0 || minY >= y1) return;

        double minX = Math.Min(x0, Math.Min(x1, x2));
        double maxX = Math.Max(x0, Math.Max(x1, x2));
        if (maxX < 0 || minX >= iw) return;

        double area = (x1 - x0) * (y2d - y0d) - (x2 - x0) * (y1d - y0d);
        if (Math.Abs(area) < 1e-12) return;
        double invArea = 1.0 / area;

        int py0 = Math.Max(y0, (int)Math.Floor(minY));
        int py1 = Math.Min(y1 - 1, (int)Math.Ceiling(maxY));
        int px0 = Math.Max(0, (int)Math.Floor(minX));
        int px1 = Math.Min(iw - 1, (int)Math.Ceiling(maxX));

        double d0 = vdp[i0], d1 = vdp[i1], d2 = vdp[i2];
        double f0x = vfx[i0], f1x = vfx[i1], f2x = vfx[i2];
        double f0y = vfy[i0], f1y = vfy[i1], f2y = vfy[i2];

        // Geometric normal, so steep faces can be shaded as faces rather than as smeared
        // height-field samples.
        double e1x = vwx[i1] - vwx[i0], e1y = vwy[i1] - vwy[i0], e1z = vwz[i1] - vwz[i0];
        double e2x = vwx[i2] - vwx[i0], e2y = vwy[i2] - vwy[i0], e2z = vwz[i2] - vwz[i0];
        double gnx = e1y * e2z - e1z * e2y;
        double gny = e1z * e2x - e1x * e2z;
        double gnz = e1x * e2y - e1y * e2x;
        double gl = Math.Sqrt(gnx * gnx + gny * gny + gnz * gnz);
        if (gl > 1e-12) { gnx /= gl; gny /= gl; gnz /= gl; }
        else { gnx = gny = gnz = 0; }

        for (int py = py0; py <= py1; py++)
        {
            double sy = py + 0.5;
            long rowBase = (long)py * iw;

            for (int px = px0; px <= px1; px++)
            {
                double sx = px + 0.5;

                double w0 = ((x1 - sx) * (y2d - sy) - (x2 - sx) * (y1d - sy)) * invArea;
                if (w0 < 0) continue;
                double w1 = ((x2 - sx) * (y0d - sy) - (x0 - sx) * (y2d - sy)) * invArea;
                if (w1 < 0) continue;
                double w2 = 1.0 - w0 - w1;
                if (w2 < 0) continue;

                double dep = w0 * d0 + w1 * d1 + w2 * d2;
                long di = rowBase + px;
                if (dep <= depth[di]) continue;

                depth[di] = (float)dep;

                double ffx = w0 * f0x + w1 * f1x + w2 * f2x;
                double ffy = w0 * f0y + w1 * f1y + w2 * f2y;

                sh.Shade(ffx, ffy, gnx, gny, gnz, out double r, out double g, out double b);
                Write(buf, (int)(di * 4), r, g, b);
            }
        }
    }

    private static void Downsample(byte[] src, byte[] dst, int outW, int outH, int ss)
    {
        int iw = outW * ss;
        Parallel.For(0, outH, y =>
        {
            for (int x = 0; x < outW; x++)
            {
                int sum0 = 0, sum1 = 0, sum2 = 0;
                for (int dy = 0; dy < ss; dy++)
                {
                    long b = ((long)(y * ss + dy) * iw + x * ss) * 4;
                    for (int dx = 0; dx < ss; dx++)
                    {
                        sum0 += src[b];
                        sum1 += src[b + 1];
                        sum2 += src[b + 2];
                        b += 4;
                    }
                }

                int n = ss * ss;
                int d = (y * outW + x) * 4;
                dst[d] = (byte)(sum0 / n);
                dst[d + 1] = (byte)(sum1 / n);
                dst[d + 2] = (byte)(sum2 / n);
                dst[d + 3] = 255;
            }
        });
    }

    // ------------------------------------------------------------------ shading

    private sealed class Shader
    {
        private readonly float[] _f;
        private readonly int _fw, _fh;
        private readonly bool _invert;
        private readonly int _slices;
        private readonly double _zk;
        private readonly float[]? _ao;
        private readonly MaterialPreset _m;

        private readonly TextureMap? _alb, _mic;
        private readonly bool _wantAlb, _wantMic;
        private readonly double _texRate, _tcos, _tsin, _microK, _survival;

        private readonly double _lx, _ly, _lz;
        private double _vx, _vy, _vz;
        private double _hx, _hy, _hz;

        public Shader(ReliefScene scene, ReliefOptions o, double zk, float[]? ao)
        {
            _f = scene.Field; _fw = scene.W; _fh = scene.H;
            _invert = o.InvertHeight; _slices = o.SliceCount;
            _zk = zk; _ao = ao; _m = o.Material;

            double el = o.LightElevationDeg * Math.PI / 180.0;
            double az = o.LightAzimuthDeg * Math.PI / 180.0;
            _lx = Math.Cos(el) * Math.Sin(az);
            _ly = Math.Cos(el) * Math.Cos(az);
            _lz = Math.Sin(el);

            _alb = _m.AlbedoTex;
            _mic = _m.MicroTex;
            _texRate = Math.Max(0.001, _m.TextureScale) / _fw;
            double trot = _m.TextureRotationDeg * Math.PI / 180.0;
            _tcos = Math.Cos(trot); _tsin = Math.Sin(trot);
            _microK = _m.MicroStrength * 0.45;
            _survival = Math.Clamp(_m.TextureEngravedSurvival, 0, 1);
            _wantAlb = _alb is not null && _m.AlbedoStrength > 0.001;
            _wantMic = _mic is not null && _microK > 0.0005;
        }

        public void SetView(double vx, double vy, double vz)
        {
            _vx = vx; _vy = vy; _vz = vz;
            double hx = _lx + vx, hy = _ly + vy, hz = _lz + vz;
            double hl = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            if (hl < 1e-9) hl = 1;
            _hx = hx / hl; _hy = hy / hl; _hz = hz / hl;
        }

        public double Height(double x, double y)
        {
            if (x < 0) x = 0; else if (x > _fw - 1) x = _fw - 1;
            if (y < 0) y = 0; else if (y > _fh - 1) y = _fh - 1;

            int x0 = (int)x, y0 = (int)y;
            int x1 = x0 + 1 < _fw ? x0 + 1 : x0;
            int y1 = y0 + 1 < _fh ? y0 + 1 : y0;
            double fx = x - x0, fy = y - y0;

            long r0 = (long)y0 * _fw, r1 = (long)y1 * _fw;
            double a = _f[r0 + x0] + (_f[r0 + x1] - _f[r0 + x0]) * fx;
            double b = _f[r1 + x0] + (_f[r1 + x1] - _f[r1 + x0]) * fx;
            double v = a + (b - a) * fy;

            if (_invert) v = 1.0 - v;

            // Quantise after interpolation, so terrace edges land where the slicer puts them.
            if (_slices > 1)
            {
                double n = _slices;
                double step = Math.Floor(v * n);
                if (step > n - 1) step = n - 1;
                v = step / (n - 1);
            }

            return v;
        }

        private double Occlusion(double x, double y)
        {
            if (_ao is null) return 1.0;
            int xi = (int)Math.Clamp(Math.Round(x), 0, _fw - 1);
            int yi = (int)Math.Clamp(Math.Round(y), 0, _fh - 1);
            return _ao[(long)yi * _fw + xi];
        }

        public void Shade(double fx, double fy, out double cr, out double cg, out double cb)
            => Shade(fx, fy, 0, 0, 0, out cr, out cg, out cb);

        /// <summary>
        /// The geometric normal of the triangle being drawn, when there is one. A height
        /// field has no vertical faces - a wall is just the gap between two adjacent
        /// samples - so on steep geometry the surface normal derived from neighbouring
        /// heights smears along the wall. Blending toward the triangle's own normal makes
        /// the sides of a raised block read as flat faces instead of streaks.
        /// </summary>
        public void Shade(double fx, double fy, double gnx, double gny, double gnz,
                          out double cr, out double cg, out double cb)
        {
            double h = Height(fx, fy);

            double dhdx = (Height(fx + 1, fy) - Height(fx - 1, fy)) * 0.5;
            double dhdy = (Height(fx, fy + 1) - Height(fx, fy - 1)) * 0.5;

            // World normal. Image y runs downward while world y runs up, hence the sign flip.
            double nx = -dhdx * _zk;
            double ny = dhdy * _zk;

            double depth = 1.0 - h;
            double texAmount = 1.0 - depth * (1.0 - _survival);

            double tu = 0, tv = 0;
            if (_wantAlb || _wantMic)
            {
                double a0 = fx * _texRate, b0 = fy * _texRate;
                tu = a0 * _tcos - b0 * _tsin;
                tv = a0 * _tsin + b0 * _tcos;
            }

            if (_wantMic)
            {
                double e = _texRate;
                double ru = e * _tcos, rv = e * _tsin;
                double su = -e * _tsin, sv = e * _tcos;

                double mxp = _mic!.SampleLum(tu + ru, tv + rv);
                double mxm = _mic.SampleLum(tu - ru, tv - rv);
                double myp = _mic.SampleLum(tu + su, tv + sv);
                double mym = _mic.SampleLum(tu - su, tv - sv);

                double k = _microK * texAmount;
                nx -= (mxp - mxm) * 0.5 * k;
                ny += (myp - mym) * 0.5 * k;
            }

            double nz = 1.0;
            double nl = Math.Sqrt(nx * nx + ny * ny + 1.0);
            nx /= nl; ny /= nl; nz /= nl;

            if (gnx != 0 || gny != 0 || gnz != 0)
            {
                double steep = 1.0 - Math.Abs(gnz);
                double wall = Math.Clamp((steep - 0.45) / 0.35, 0, 1);
                wall = wall * wall * (3.0 - 2.0 * wall);

                if (wall > 0)
                {
                    double s = (gnx * _vx + gny * _vy + gnz * _vz) < 0 ? -1.0 : 1.0;
                    nx = nx * (1 - wall) + gnx * s * wall;
                    ny = ny * (1 - wall) + gny * s * wall;
                    nz = nz * (1 - wall) + gnz * s * wall;
                    double l2 = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (l2 > 1e-9) { nx /= l2; ny /= l2; nz /= l2; }
                }
            }

            double ndv = nx * _vx + ny * _vy + nz * _vz;
            double under = 1.0;
            if (ndv < 0)
            {
                // Looking at the underside of the sheet. Flip so it is lit sanely and darken,
                // rather than letting it render as a black hole at grazing angles.
                nx = -nx; ny = -ny; nz = -nz;
                ndv = -ndv;
                under = 0.30;
            }

            double ao = Occlusion(fx, fy);

            double ndl = nx * _lx + ny * _ly + nz * _lz;
            if (ndl < 0) ndl = 0;
            double ndh = nx * _hx + ny * _hy + nz * _hz;
            if (ndh < 0) ndh = 0;

            double gloss = _m.FieldGloss + (_m.EngravedGloss - _m.FieldGloss) * depth;
            double specStr = _m.FieldSpec + (_m.EngravedSpec - _m.FieldSpec) * depth;
            double tone = 1.0 + (_m.EngravedTone - 1.0) * depth;

            double ar = _m.AlbedoR, ag = _m.AlbedoG, ab = _m.AlbedoB;
            double sr = _m.SpecR, sg = _m.SpecG, sb = _m.SpecB;

            if (_wantAlb)
            {
                _alb!.Sample(tu, tv, out double tr, out double tg, out double tb);
                double s = Math.Clamp(_m.AlbedoStrength * texAmount, 0, 1);
                ar = ar * (1 - s) + tr * s;
                ag = ag * (1 - s) + tg * s;
                ab = ab * (1 - s) + tb * s;

                // On a metal the diffuse colour barely shows, so let the texture tint the
                // specular as well or a brass photo would have almost no visible effect.
                if (_m.Metallic)
                {
                    double k = s * 0.7;
                    sr = sr * (1 - k) + tr * k;
                    sg = sg * (1 - k) + tg * k;
                    sb = sb * (1 - k) + tb * k;
                }
            }

            ar *= tone; ag *= tone; ab *= tone;

            double spec = Math.Pow(ndh, Math.Max(1.0, gloss)) * specStr;

            // Environment reflection about the surface normal. Using the full reflected
            // direction rather than just how flat the surface is keeps micro-surface detail
            // visible everywhere instead of only in the specular dot.
            double rfx = 2.0 * ndv * nx - _vx;
            double rfy = 2.0 * ndv * ny - _vy;
            double rfz = 2.0 * ndv * nz - _vz;

            double t = Math.Clamp((rfz * 0.55 + rfy * 0.70) * 0.5 + 0.44, 0, 1);
            t = t * t * (3.0 - 2.0 * t);

            double envR = FloorR + (SkyR - FloorR) * t;
            double envG = FloorG + (SkyG - FloorG) * t;
            double envB = FloorB + (SkyB - FloorB) * t;

            if (_m.Metallic)
            {
                // Metals have almost no diffuse, so the engraved-floor tone has to act on the
                // reflection itself. An oxidised, frosted floor reflects less than the
                // polished field; without this the difference would be invisible on exactly
                // the materials it matters most for.
                double refl = Math.Clamp(tone, 0.04, 3.0);
                cr = sr * (spec + envR * _m.EnvStrength * ao) * refl + ar * ndl * 0.12;
                cg = sg * (spec + envG * _m.EnvStrength * ao) * refl + ag * ndl * 0.12;
                cb = sb * (spec + envB * _m.EnvStrength * ao) * refl + ab * ndl * 0.12;
            }
            else
            {
                // On a dielectric the environment is a weak Fresnel reflection, not a
                // constant. Adding it flat pours untinted grey into every channel and washes
                // the colour out - oak ends up looking like driftwood.
                double f = 1.0 - ndv;
                double fres = 0.045 + 0.955 * (f * f * f * f * f);
                double env = _m.EnvStrength * fres * ao;

                // Occlusion is applied to the direct term too. Strictly a cheat, but reading
                // form is the point of this view and without it contact shadows disappear.
                double lit = _m.Ambient * ao + ndl * 0.92 * (0.40 + 0.60 * ao);

                cr = ar * lit + sr * (spec * 0.65 + envR * env);
                cg = ag * lit + sg * (spec * 0.65 + envG * env);
                cb = ab * lit + sb * (spec * 0.65 + envB * env);
            }

            cr *= under; cg *= under; cb *= under;
        }
    }

    // ------------------------------------------------------------------ output

    private static void Write(byte[] bgra, int d, double r, double g, double b)
    {
        if (r < 0) r = 0;
        if (g < 0) g = 0;
        if (b < 0) b = 0;

        // Highlight shoulder applied to luminance, with the three channels scaled by the same
        // factor. Rolling each channel off independently pulls the brightest one down hardest
        // and quietly desaturates everything, which is how a warm brass ends up olive drab.
        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (lum > 0)
        {
            double k = 1.0 / (1.0 + lum * 0.42);
            r *= k; g *= k; b *= k;
        }

        bgra[d] = ToByte(b);
        bgra[d + 1] = ToByte(g);
        bgra[d + 2] = ToByte(r);
        bgra[d + 3] = 255;
    }

    private static byte ToByte(double v)
    {
        if (v <= 0) return 0;
        if (v > 1) v = 1;
        v = Math.Pow(v, 1.0 / 2.2);        // to display gamma
        return (byte)Math.Clamp(v * 255.0 + 0.5, 0, 255);
    }
}
