using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DepthView.Imaging;
using DepthView.Rendering;

namespace DepthView.Views;

public partial class ReliefWindow : Window
{
    private readonly ReliefScene _scene;
    private readonly int _fw, _fh;
    private readonly string _caption;

    private double _zoom;          // 0 means "fit on next render"
    private double _panX, _panY;

    private bool _rendering, _dirty, _wantFast, _syncing;
    private readonly DispatcherTimer _settle;

    private Point _dragStart;
    private bool _lightDrag, _panDrag, _orbitDrag;
    private double _dragAz, _dragEl, _dragPanX, _dragPanY, _dragYaw, _dragPitch;

    public ReliefWindow(ImageData image, string caption)
    {
        InitializeComponent();

        _caption = caption;
        var field = ReliefRenderer.BuildHeights(image, 1400, out _fw, out _fh);
        _scene = new ReliefScene(field, _fw, _fh);

        RebuildMaterialCombo(0);

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        _settle.Tick += (_, _) => { _settle.Stop(); Request(false); };

        MaterialCombo.SelectionChanged += (_, _) => { SyncFromMaterial(); Change(); };

        AzSlider.PropertyChanged += OnSliderChanged;
        ElSlider.PropertyChanged += OnSliderChanged;
        AoSlider.PropertyChanged += OnSliderChanged;
        ExagSlider.PropertyChanged += OnSliderChanged;
        SliceSlider.PropertyChanged += OnSliderChanged;
        TexScaleSlider.PropertyChanged += OnSliderChanged;
        TexRotSlider.PropertyChanged += OnSliderChanged;
        AlbStrengthSlider.PropertyChanged += OnSliderChanged;
        MicroStrengthSlider.PropertyChanged += OnSliderChanged;
        SurvivalSlider.PropertyChanged += OnSliderChanged;
        YawSlider.PropertyChanged += OnSliderChanged;
        PitchSlider.PropertyChanged += OnSliderChanged;

        OrbitCheck.IsCheckedChanged += (_, _) =>
        {
            YawSlider.IsEnabled = PitchSlider.IsEnabled = OrbitCheck.IsChecked == true;
            Change();
        };

        TopViewButton.Click += (_, _) =>
        {
            _syncing = true;
            YawSlider.Value = 0;
            PitchSlider.Value = 90;
            OrbitCheck.IsChecked = false;
            YawSlider.IsEnabled = PitchSlider.IsEnabled = false;
            _syncing = false;
            Change();
        };

        InvertCheck.IsCheckedChanged += (_, _) => Change();
        SliceCheck.IsCheckedChanged += (_, _) =>
        {
            SliceSlider.IsEnabled = SliceCheck.IsChecked == true;
            Change();
        };
        GeneratedCombo.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var m = Current;
            string? want = GeneratedName(GeneratedCombo.SelectedIndex);
            if (!string.Equals(want, m.ProceduralTexture, StringComparison.OrdinalIgnoreCase))
            {
                m.ProceduralTexture = want;
                m.InvalidateTextures();
            }
            SyncFromMaterial();
            Change();
        };

        LoadAlbedoButton.Click += async (_, _) => await LoadTextureAsync(albedo: true);
        LoadMicroButton.Click += async (_, _) => await LoadTextureAsync(albedo: false);
        ClearAlbedoButton.Click += (_, _) => ClearTexture(albedo: true);
        ClearMicroButton.Click += (_, _) => ClearTexture(albedo: false);

        SaveMaterialsButton.Click += (_, _) => SaveMaterials();
        ResetMaterialsButton.Click += (_, _) =>
        {
            int keep = MaterialCombo.SelectedIndex;
            MaterialLibrary.ResetToBuiltins();
            RebuildMaterialCombo(keep);
            SyncFromMaterial();
            Change();
        };

        ResetViewButton.Click += (_, _) => { ResetView(); Change(); };
        SaveButton.Click += async (_, _) => await SaveRenderAsync();

        Preview.PointerPressed += OnPointerPressed;
        Preview.PointerMoved += OnPointerMoved;
        Preview.PointerReleased += OnPointerReleased;
        Preview.PointerWheelChanged += OnWheel;

        PreviewHost.SizeChanged += (_, _) => Change();

        Opened += (_, _) =>
        {
            if (MaterialLibrary.LoadError is { } err) TextureError.Text = err;

            if (Program.StartupYaw is { } y0 && Program.StartupPitch is { } p0)
            {
                _syncing = true;
                YawSlider.Value = Math.Clamp(y0, YawSlider.Minimum, YawSlider.Maximum);
                PitchSlider.Value = Math.Clamp(p0, PitchSlider.Minimum, PitchSlider.Maximum);
                OrbitCheck.IsChecked = true;
                _syncing = false;
            }

            SyncFromMaterial();
            Change();
        };
    }

    private static string? GeneratedName(int index) => index switch
    {
        1 => "brushed",
        2 => "wood",
        3 => "speckle",
        _ => null
    };

    private static int GeneratedIndex(string? name) => (name ?? "").ToLowerInvariant() switch
    {
        "brushed" => 1,
        "wood" => 2,
        "speckle" => 3,
        _ => 0
    };

    private MaterialPreset Current
    {
        get
        {
            var list = MaterialLibrary.Presets;
            int i = Math.Clamp(MaterialCombo.SelectedIndex, 0, list.Count - 1);
            return list[i];
        }
    }

    private void RebuildMaterialCombo(int select)
    {
        _syncing = true;
        MaterialCombo.Items.Clear();
        foreach (var m in MaterialLibrary.Presets)
            MaterialCombo.Items.Add(new ComboBoxItem { Content = m.Name });
        MaterialCombo.SelectedIndex = Math.Clamp(select, 0, MaterialLibrary.Presets.Count - 1);
        _syncing = false;
    }

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty) Change();
    }

    // ------------------------------------------------------------------ material sync

    /// <summary>Push the panel's texture controls into the selected material.</summary>
    private void SyncToMaterial()
    {
        if (_syncing) return;
        var m = Current;
        m.TextureScale = TexScaleSlider.Value;
        m.TextureRotationDeg = TexRotSlider.Value;
        m.AlbedoStrength = AlbStrengthSlider.Value;
        m.MicroStrength = MicroStrengthSlider.Value;
        m.TextureEngravedSurvival = SurvivalSlider.Value;
    }

    /// <summary>Pull the selected material's settings back into the panel.</summary>
    private void SyncFromMaterial()
    {
        _syncing = true;
        var m = Current;

        TexScaleSlider.Value = Math.Clamp(m.TextureScale, TexScaleSlider.Minimum, TexScaleSlider.Maximum);
        TexRotSlider.Value = Math.Clamp(m.TextureRotationDeg, TexRotSlider.Minimum, TexRotSlider.Maximum);
        AlbStrengthSlider.Value = Math.Clamp(m.AlbedoStrength, 0, 1);
        MicroStrengthSlider.Value = Math.Clamp(m.MicroStrength, 0, MicroStrengthSlider.Maximum);
        SurvivalSlider.Value = Math.Clamp(m.TextureEngravedSurvival, 0, 1);
        GeneratedCombo.SelectedIndex = GeneratedIndex(m.ProceduralTexture);

        bool gen = !string.IsNullOrWhiteSpace(m.ProceduralTexture);
        bool genColours = gen && m.ProceduralTexture is "wood" or "speckle";

        AlbedoLabel.Text = !string.IsNullOrWhiteSpace(m.AlbedoTexturePath)
            ? Path.GetFileName(m.AlbedoTexturePath)
            : genColours ? "none - using the generated texture"
            : "none - using this material's flat colour";

        MicroLabel.Text = !string.IsNullOrWhiteSpace(m.MicroTexturePath)
            ? Path.GetFileName(m.MicroTexturePath)
            : gen ? "none - using the generated texture"
            : "none - perfectly smooth surface";

        _syncing = false;
        UpdateLabels();
    }

    private async Task LoadTextureAsync(bool albedo)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = albedo ? "Choose a colour texture" : "Choose a surface relief texture",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = ImageLoader.SupportedPatterns.Split(';') },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });

        var picked = files.FirstOrDefault();
        if (picked is null) return;

        string? path = picked.TryGetLocalPath();
        if (path is null)
        {
            TextureError.Text = "That file has to live on the local filesystem so it can be " +
                                "re-read next session; copy it locally first.";
            return;
        }

        var m = Current;
        if (albedo)
        {
            m.AlbedoTexturePath = path;
            if (m.AlbedoStrength <= 0.001) m.AlbedoStrength = 1.0;
        }
        else
        {
            m.MicroTexturePath = path;
            if (m.MicroStrength <= 0.001) m.MicroStrength = 0.8;
        }

        m.InvalidateTextures();
        SyncFromMaterial();
        Change();
    }

    private void ClearTexture(bool albedo)
    {
        var m = Current;
        if (albedo) m.AlbedoTexturePath = null;
        else m.MicroTexturePath = null;

        m.InvalidateTextures();
        TextureError.Text = "";
        SyncFromMaterial();
        Change();
    }

    private void SaveMaterials()
    {
        try
        {
            SyncToMaterial();
            MaterialLibrary.Save(MaterialLibrary.Presets);
            TextureError.Text = "";
            StatusText.Text = $"Materials saved to {MaterialLibrary.DefaultPath}";
        }
        catch (Exception ex)
        {
            TextureError.Text = "Could not save materials: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ render loop

    private void Change()
    {
        SyncToMaterial();
        UpdateLabels();
        Request(true);
        _settle.Stop();
        _settle.Start();
    }

    private void UpdateLabels()
    {
        AzLabel.Text = $"Light direction   {AzSlider.Value:F0} deg";
        ElLabel.Text = $"Light elevation   {ElSlider.Value:F0} deg";
        AoLabel.Text = $"Ambient occlusion   {AoSlider.Value:F2}";
        ExagLabel.Text = $"Vertical exaggeration   {ExagSlider.Value:F2}x";
        SliceLabel.Text = SliceCheck.IsChecked == true
            ? $"Steps   {SliceSlider.Value:F0}"
            : "Steps   (continuous)";

        YawLabel.Text = $"Orbit   {YawSlider.Value:F0} deg";
        PitchLabel.Text = PitchSlider.Value >= 89.5
            ? "Camera elevation   90 deg (straight down)"
            : $"Camera elevation   {PitchSlider.Value:F0} deg";

        TexScaleLabel.Text = $"Texture scale   {TexScaleSlider.Value:F2} across the piece";
        TexRotLabel.Text = $"Texture rotation   {TexRotSlider.Value:F0} deg";
        AlbStrengthLabel.Text = $"Colour strength   {AlbStrengthSlider.Value:F2}";
        MicroStrengthLabel.Text = $"Surface relief strength   {MicroStrengthSlider.Value:F2}";
        SurvivalLabel.Text = $"Texture survives engraving   {SurvivalSlider.Value * 100:F0}%";
    }

    private void ResetView()
    {
        _zoom = 0;
        _panX = _panY = 0;
        _syncing = true;
        YawSlider.Value = 0;
        PitchSlider.Value = 62;
        _syncing = false;
    }

    private async void Request(bool fast)
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
        catch (Exception ex)
        {
            StatusText.Text = "Render failed: " + ex.Message;
        }
        finally
        {
            _rendering = false;
        }
    }

    private async Task RenderOnceAsync(bool fast)
    {
        double hostW = PreviewHost.Bounds.Width, hostH = PreviewHost.Bounds.Height;
        if (hostW < 8 || hostH < 8 || _scene.Field.Length == 0) return;

        int q = fast ? 2 : 1;
        int bw = Math.Max(32, (int)(hostW / q));
        int bh = Math.Max(32, (int)(hostH / q));

        if (_zoom <= 0)
            _zoom = Math.Min(hostW / _fw, hostH / _fh) * 0.94;

        var material = Current;

        var o = new ReliefOptions
        {
            Material = material,
            LightAzimuthDeg = AzSlider.Value,
            LightElevationDeg = ElSlider.Value,
            AoStrength = AoSlider.Value,
            Exaggeration = ExagSlider.Value,
            InvertHeight = InvertCheck.IsChecked == true,
            SliceCount = SliceCheck.IsChecked == true ? (int)SliceSlider.Value : 0,
            Zoom = _zoom / q,
            PanX = _panX,
            PanY = _panY,
            Quality = q,
            Orbit = OrbitCheck.IsChecked == true,
            YawDeg = YawSlider.Value,
            PitchDeg = PitchSlider.Value,
            // Mesh density only costs silhouette accuracy - shading is sampled per pixel from
            // the height field - so it can drop a long way while dragging without looking soft.
            MeshResolution = fast ? 192 : 720,
            Supersample = fast ? 1 : 2
        };

        var buf = new byte[(long)bw * bh * 4];
        var sw = Stopwatch.StartNew();
        await Task.Run(() => ReliefRenderer.Render(buf, bw, bh, _scene, o));
        sw.Stop();

        var bmp = new WriteableBitmap(new PixelSize(bw, bh), new Vector(96, 96),
            PixelFormats.Bgra8888, AlphaFormat.Opaque);

        using (var fb = bmp.Lock())
        {
            int rowBytes = bw * 4;
            for (int y = 0; y < bh; y++)
                Marshal.Copy(buf, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        }

        Preview.Source = bmp;

        if (material.TextureError is { } te) TextureError.Text = te;
        else if (TextureError.Text?.StartsWith("Colour texture") == true
              || TextureError.Text?.StartsWith("Surface texture") == true) TextureError.Text = "";

        string tex = material.AlbedoTex is null && material.MicroTex is null
            ? "untextured"
            : string.Join(" + ", new[]
              {
                  material.AlbedoTex is null ? null : "colour",
                  material.MicroTex is null ? null : "surface"
              }.Where(s => s is not null)) + " texture";

        string slices = o.SliceCount > 0 ? $"{o.SliceCount} steps" : "continuous";
        string cam = o.Orbit
            ? $"3D  orbit {o.YawDeg:F0} deg, elevation {o.PitchDeg:F0} deg, mesh {o.MeshResolution}"
            : "flat top view (pixel exact)";

        StatusText.Text =
            $"{_caption}   |   height field {_fw} x {_fh}   |   render {bw} x {bh} " +
            $"{(fast ? "(draft)" : "(full)")} in {sw.ElapsedMilliseconds} ms   |   " +
            $"zoom {_zoom:F2}x   |   {cam}   |   {slices}   |   {tex}";
    }

    // ------------------------------------------------------------------ input

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Preview).Properties;
        _dragStart = e.GetPosition(Preview);

        if (props.IsMiddleButtonPressed)
        {
            ResetView();
            Change();
            return;
        }

        if (props.IsRightButtonPressed)
        {
            _panDrag = true;
            _dragPanX = _panX;
            _dragPanY = _panY;
        }
        else if (props.IsLeftButtonPressed)
        {
            // Left drag orbits the camera, which is what people reach for first. Moving the
            // light is the same gesture with Shift held, and the sliders still do both.
            bool wantLight = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                             || OrbitCheck.IsChecked != true;

            if (wantLight)
            {
                _lightDrag = true;
                _dragAz = AzSlider.Value;
                _dragEl = ElSlider.Value;
            }
            else
            {
                _orbitDrag = true;
                _dragYaw = YawSlider.Value;
                _dragPitch = PitchSlider.Value;
            }
        }

        e.Pointer.Capture(Preview);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_lightDrag && !_panDrag && !_orbitDrag) return;

        var p = e.GetPosition(Preview);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;

        if (_orbitDrag)
        {
            double yaw = _dragYaw + dx * 0.42;
            while (yaw > 180) yaw -= 360;
            while (yaw < -180) yaw += 360;

            _syncing = true;
            YawSlider.Value = yaw;
            PitchSlider.Value = Math.Clamp(_dragPitch + dy * 0.32,
                PitchSlider.Minimum, PitchSlider.Maximum);
            _syncing = false;
            Change();
        }
        else if (_lightDrag)
        {
            double az = _dragAz + dx * 0.55;
            az %= 360;
            if (az < 0) az += 360;
            AzSlider.Value = az;
            ElSlider.Value = Math.Clamp(_dragEl - dy * 0.28, ElSlider.Minimum, ElSlider.Maximum);
        }
        else
        {
            _panX = _dragPanX - dx / Math.Max(0.01, _zoom);
            _panY = _dragPanY - dy / Math.Max(0.01, _zoom);
            Change();
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_lightDrag || _panDrag || _orbitDrag)
        {
            _lightDrag = _panDrag = _orbitDrag = false;
            e.Pointer.Capture(null);
            Request(false);
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        double hostW = PreviewHost.Bounds.Width, hostH = PreviewHost.Bounds.Height;
        if (hostW < 8 || _zoom <= 0) return;

        var p = e.GetPosition(Preview);

        // Field coordinate currently under the cursor, kept fixed across the zoom.
        double fx = (p.X - hostW / 2) / _zoom + _panX + _fw / 2.0;
        double fy = (p.Y - hostH / 2) / _zoom + _panY + _fh / 2.0;

        double factor = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;
        _zoom = Math.Clamp(_zoom * factor, 0.02, 200);

        _panX = fx - _fw / 2.0 - (p.X - hostW / 2) / _zoom;
        _panY = fy - _fh / 2.0 - (p.Y - hostH / 2) / _zoom;

        Change();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ export

    private async Task SaveRenderAsync()
    {
        if (Preview.Source is not Bitmap bmp) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save relief render",
            SuggestedFileName = "relief-preview.png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } }
        });

        if (file is null) return;

        await using var s = await file.OpenWriteAsync();
        bmp.Save(s);
        StatusText.Text = $"Render saved as {file.Name}.";
    }
}
