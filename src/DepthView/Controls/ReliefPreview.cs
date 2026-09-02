using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DepthView.Rendering;

namespace DepthView.Controls;

/// <summary>
/// Camera, light and surface settings shared by every pane showing the same piece.
///
/// One instance is handed to both panes of the tuning dialog on purpose. An A/B comparison
/// where the two halves can drift apart in orbit, zoom or light angle is not a comparison at
/// all - it is two pictures, and the eye will happily attribute a lighting difference to the
/// tuning. Sharing the object makes divergence impossible rather than merely unlikely.
/// </summary>
public sealed class ReliefViewSettings
{
    public double LightAzimuthDeg = 315;
    public double LightElevationDeg = 42;
    public double AoStrength = 1.0;

    /// <summary>
    /// How deep, in millimetres, the full black-to-white range should be drawn.
    ///
    /// The renderer's own exaggeration figure is a ratio to the field width and means nothing
    /// physical, so this is stated in millimetres against <see cref="BlankMm"/> and converted
    /// per pane - the two panes can have different canvas sizes once fitting has grown one of
    /// them, and both still represent the same blank.
    ///
    /// This changes the picture and nothing else. No grey level, no written file, and no number
    /// in the results card depends on it.
    /// </summary>
    public double ApparentDepthMm = 0.40;

    /// <summary>Diameter of the blank the map is drawn on, which the short side spans.</summary>
    public double BlankMm = 40.0;

    /// <summary>0 = continuous surface. Above 0, quantise to this many steps.</summary>
    public int SliceCount;

    public bool Orbit = true;
    public double YawDeg;
    public double PitchDeg = 62;

    /// <summary>Multiplier on each pane's own fit-to-pane scale, so panes of different
    /// field sizes still both fill their box at 1.0.</summary>
    public double ZoomMul = 1.0;

    public double PanX, PanY;

    public int MaterialIndex;

    public MaterialPreset Material
    {
        get
        {
            var list = MaterialLibrary.Presets;
            return list[Math.Clamp(MaterialIndex, 0, list.Count - 1)];
        }
    }

    public void ResetView()
    {
        ZoomMul = 1.0;
        PanX = PanY = 0;
        YawDeg = 0;
        PitchDeg = 62;
    }
}

/// <summary>
/// A lit relief view of one height field, driven by <see cref="ReliefRenderer"/>.
///
/// This deliberately does not reuse ReliefWindow's render loop. That window binds its loop to a
/// dozen named sliders it also owns, and prising them apart would mean refactoring a window that
/// works, on the way to a feature that does not need it. What actually matters is shared already:
/// the renderer, the scene and the occlusion cache. Roughly seventy lines of loop plumbing are
/// duplicated here, which is a smaller price than a regression in the standalone viewer.
///
/// Everything expensive lives in <see cref="ReliefScene"/>, which caches ambient occlusion. A new
/// field means a new scene and a recomputed occlusion map, so panes swap their field only when the
/// surface really changed - not on every camera nudge.
/// </summary>
public class ReliefPreview : UserControl
{
    private readonly Image _image = new()
    {
        Stretch = Avalonia.Media.Stretch.None,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private ReliefScene? _scene;
    private int _fw, _fh;

    private bool _rendering, _dirty, _wantFast;
    private readonly DispatcherTimer _settle;

    private Point _dragStart;
    private bool _lightDrag, _panDrag, _orbitDrag;
    private double _dragAz, _dragEl, _dragPanX, _dragPanY, _dragYaw, _dragPitch;

    /// <summary>Shared with any sibling pane. Never null in practice; defaulted so the
    /// designer and any stray early render cannot fault.</summary>
    public ReliefViewSettings Settings { get; set; } = new();

    /// <summary>Raised when a drag or wheel changed the shared settings, so the window can
    /// re-render the other pane too.</summary>
    public event EventHandler? ViewChanged;

    public ReliefPreview()
    {
        Content = _image;
        ClipToBounds = true;
        Focusable = false;

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        _settle.Tick += (_, _) => { _settle.Stop(); Request(false); };

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnWheel;

        SizeChanged += (_, _) => Request(true);
    }

    /// <summary>
    /// Replace the surface. Cheap to call with an unchanged field only in the sense that it is
    /// not free: it rebuilds the scene and throws away the occlusion cache, so callers should
    /// only call it when the buffer actually changed.
    /// </summary>
    public void SetField(float[] field, int w, int h)
    {
        _fw = w;
        _fh = h;
        _scene = field.Length == 0 ? null : new ReliefScene(field, w, h);
        Request(true);
    }

    public void Clear()
    {
        _scene = null;
        _image.Source = null;
    }

    /// <summary>Queue a render. <paramref name="fast"/> draws draft quality now and schedules a
    /// full-quality pass once the user stops moving.</summary>
    public void Request(bool fast)
    {
        RequestCore(fast);
        if (fast)
        {
            _settle.Stop();
            _settle.Start();
        }
    }

    private async void RequestCore(bool fast)
    {
        _wantFast = _wantFast || fast;

        if (_rendering) { _dirty = true; return; }
        _rendering = true;

        try
        {
            do
            {
                _dirty = false;
                bool f = _wantFast;
                _wantFast = false;
                await RenderOnceAsync(f);
            }
            while (_dirty);
        }
        catch
        {
            // A preview pane is not worth taking the dialog down for. The flat panes and every
            // number in the results card are computed elsewhere and remain correct.
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task RenderOnceAsync(bool fast)
    {
        var scene = _scene;
        if (scene is null) return;

        double hostW = Bounds.Width, hostH = Bounds.Height;
        if (hostW < 8 || hostH < 8) return;

        var s = Settings;

        int q = fast ? 2 : 1;
        int bw = Math.Max(32, (int)(hostW / q));
        int bh = Math.Max(32, (int)(hostH / q));

        // Each pane fits its own field before the shared multiplier is applied. Fitting grows
        // the tuned canvas, so the two fields are not always the same size, and forcing one
        // scale on both would shrink the tuned pane for reasons that have nothing to do with
        // the tuning.
        //
        // A tilted camera needs more slack than a flat one, and the tuned pane is exactly where
        // that bites: a corrected map is often three times deeper than the source, so its
        // silhouette stands taller on screen and runs out of the frame while the original still
        // has room. Since both panes share a camera, the headroom has to be enough for the
        // deeper of the two, or the A/B comparison shows a clipped surface against a whole one
        // and the clipping reads as a defect in the tuning.
        double slack = s.Orbit ? 0.82 : 0.94;
        double fit = Math.Min(hostW / Math.Max(1, _fw), hostH / Math.Max(1, _fh)) * slack;

        var o = new ReliefOptions
        {
            Material = s.Material,
            LightAzimuthDeg = s.LightAzimuthDeg,
            LightElevationDeg = s.LightElevationDeg,
            AoStrength = s.AoStrength,
            Exaggeration = ExaggerationForDepth(s),
            SliceCount = s.SliceCount,
            Zoom = fit * s.ZoomMul / q,
            PanX = s.PanX,
            PanY = s.PanY,
            Quality = q,
            Orbit = s.Orbit,
            YawDeg = s.YawDeg,
            PitchDeg = s.PitchDeg,
            MeshResolution = fast ? 192 : 720,
            Supersample = fast ? 1 : 2
        };

        var buf = new byte[(long)bw * bh * 4];
        await Task.Run(() => ReliefRenderer.Render(buf, bw, bh, scene, o));

        var bmp = new WriteableBitmap(new PixelSize(bw, bh), new Vector(96, 96),
                                      PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            int rowBytes = bw * 4;
            for (int y = 0; y < bh; y++)
                Marshal.Copy(buf, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }

        _image.Width = bw * q;
        _image.Height = bh * q;
        _image.Source = bmp;
    }

    /// <summary>
    /// Millimetres of wanted depth into the renderer's own exaggeration figure.
    ///
    /// The renderer draws the full height range as field-width / 8 multiplied by that figure,
    /// which is a ratio to the picture and has no physical meaning at all - on a 40 mm blank
    /// its 1.0 draws a relief 5 mm deep, deeper than the blank is thick. Inverting it here is
    /// what lets the dialog talk in millimetres, and it has to be done per pane because the
    /// blank spans the short side of whatever canvas that pane ended up with.
    /// </summary>
    private double ExaggerationForDepth(ReliefViewSettings s)
    {
        if (s.ApparentDepthMm <= 0 || s.BlankMm <= 0 || _fw <= 0 || _fh <= 0) return 0;

        double mmPerFieldPixel = s.BlankMm / Math.Min(_fw, _fh);
        double wantedPixels = s.ApparentDepthMm / mmPerFieldPixel;
        return wantedPixels / (_fw / 8.0);
    }

    // ------------------------------------------------------------------ input

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_scene is null) return;

        var props = e.GetCurrentPoint(this).Properties;
        _dragStart = e.GetPosition(this);
        var s = Settings;

        if (props.IsMiddleButtonPressed)
        {
            s.ResetView();
            Changed();
            return;
        }

        if (props.IsRightButtonPressed)
        {
            _panDrag = true;
            _dragPanX = s.PanX;
            _dragPanY = s.PanY;
        }
        else if (props.IsLeftButtonPressed)
        {
            // Orbiting is what someone expects a 3D view to do under the mouse, so it gets the
            // plain drag whenever the camera is free to move. The light stays reachable with a
            // modifier rather than being given the primary gesture it had in the standalone
            // viewer, where there was no orbit to compete with.
            bool wantLight = e.KeyModifiers.HasFlag(KeyModifiers.Control) || !s.Orbit;
            if (wantLight)
            {
                _lightDrag = true;
                _dragAz = s.LightAzimuthDeg;
                _dragEl = s.LightElevationDeg;
            }
            else
            {
                _orbitDrag = true;
                _dragYaw = s.YawDeg;
                _dragPitch = s.PitchDeg;
            }
        }

        e.Pointer.Capture(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_lightDrag && !_panDrag && !_orbitDrag) return;

        var p = e.GetPosition(this);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        var s = Settings;

        if (_lightDrag)
        {
            s.LightAzimuthDeg = ((_dragAz + dx * 0.5) % 360 + 360) % 360;
            s.LightElevationDeg = Math.Clamp(_dragEl - dy * 0.3, 5, 85);
        }
        else if (_orbitDrag)
        {
            s.YawDeg = ((_dragYaw + dx * 0.4) % 360 + 360) % 360;
            s.PitchDeg = Math.Clamp(_dragPitch - dy * 0.3, 8, 90);
        }
        else
        {
            s.PanX = _dragPanX + dx;
            s.PanY = _dragPanY + dy;
        }

        Changed();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_lightDrag && !_panDrag && !_orbitDrag) return;

        _lightDrag = _panDrag = _orbitDrag = false;
        e.Pointer.Capture(null);
        Changed();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_scene is null) return;

        Settings.ZoomMul = Math.Clamp(Settings.ZoomMul * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.15, 14.0);
        Changed();
        e.Handled = true;
    }

    private void Changed()
    {
        Request(true);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}
