using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DepthView.Ablation;
using LumosUltra.Camera;
using LumosUltra.Client;
using LumosUltra.Client.Models;
using LumosUltra.Desktop;
using LumosUltra.Jobs;

namespace DepthView.Views;

/// <summary>
/// Cuts one coupon on the Lumos Ultra: check the laser and door, see the coupon's footprint on
/// the laser camera's picture and drag it onto the blank, audit the job against the study's
/// energy ceiling, send and arm it, start it (from here or on the machine), follow it to the
/// end. Returns true when the coupon was cut, and the database then records it as engraved
/// with the bed position and the job file that was sent.
///
/// Placement works the way MOPAChroma Atlas does it, with the same shared code: the camera
/// is calibrated once to the bed (CameraCalibrationWindow), after which a picture shows where
/// the job lands. Without a calibration, sending needs an explicit "I have checked the
/// position myself".
/// </summary>
public sealed class AblationSendWindow : Window
{
    private enum Phase { Ready, Armed, Running, Finished, Failed }

    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Ok = B("#5CC28A"), Busy = B("#E3A640"), Bad = B("#EF6A70");

    private readonly Study _study;
    private readonly Coupon _coupon;
    private readonly AblationDatabase _db;
    private LumosSettings _settings = LumosSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private Phase _phase = Phase.Ready;
    private double _cx = MakeItJob.HomeX, _cy = MakeItJob.HomeY;
    private PointD _dragStartBed;
    private (double x, double y) _dragStartCentre;
    private bool _placementSeen, _syncing, _zoomed;
    private DateTime _runStarted;
    private string? _gcode;

    private readonly TextBox _address = new();
    private readonly PhotoView _photo = new();
    private readonly NumericUpDown _centreX = Num(), _centreY = Num();
    private readonly CheckBox _marks = new() { Content = "Engrave the corner crosses", IsChecked = true };
    private readonly CheckBox _blind = new() { Content = "I have checked where it will engrave myself (send without the camera)" };
    private readonly CheckBox _confirm = new() { Content = "The blank is on the bed, the door is closed and I am ready" };
    private readonly TextBlock _step1 = Text("", 12, Ink), _step2 = Text("", 12, Ink), _step3 = Text("", 12, Ink), _step4 = Text("", 12, Ink),
        _placeText = Text("", 11, Muted), _status = Text("", 12, Bright), _auditText = Text("", 11, Muted);
    private readonly Button _check, _photoBtn, _calibrate, _centre, _send, _startHere, _stop, _saveJob, _close;

    public AblationSendWindow(Study study, Coupon coupon, AblationDatabase db)
    {
        _study = study; _coupon = coupon; _db = db;
        Title = $"Cut coupon DV{coupon.Number} - {study.Name}";
        Width = 1280; Height = 860; MinWidth = 1000; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;
        _address.Text = _settings.Address;

        _check = MakeButton("Check the laser", "Look for the laser at this address and confirm the door is closed.");
        _photoBtn = MakeButton("Take a picture", "Photograph the bed with the laser's camera and draw the coupon where it will engrave.");
        _calibrate = MakeButton("Calibrate the camera...", "Engrave nine small crosses on a scrap and click them, once; after that every job is shown on the picture.");
        _centre = MakeButton("Centre on the bed", "Put the coupon's centre at X 105, Y 105 (MakeIt's home).");
        _send = MakeButton("Audit, send and arm", "Build the job, check its energy per square millimetre against the study's ceiling, check the door, send it and arm it. Nothing fires until Start.");
        _startHere = MakeButton("Start from here", "Start the armed job without pressing the machine's button. Only with the confirmation ticked.");
        _stop = MakeButton("Stop job", "Cancel the running job. The machine's own stop button also works.");
        _saveJob = MakeButton("Save the job file...", "Write the audited G-code to a file without sending it, for a look or for sending another way.");
        _close = MakeButton("Close", "Close this window.");
        _startHere.IsEnabled = false; _stop.IsVisible = false;

        var left = new StackPanel { Spacing = 8, Margin = new Thickness(14) };
        left.Children.Add(Head($"Coupon DV{coupon.Number}"));
        left.Children.Add(Text($"{study.Material.Summary} - {study.Coupon.Summary}\n{coupon.Patches.Count} patches, about {CouponJob.Estimate(study, coupon).TotalMinutes:0} min.", 12, Ink));
        left.Children.Add(Text("Focus the machine on the blank as for any job first: every patch is cut at that focus, and the job moves nothing in Z.", 11, Muted));

        left.Children.Add(Head("1. The laser"));
        left.Children.Add(Row("Address", _address, "The laser's address: 192.168.42.1:8080 over USB, or its Wi-Fi address."));
        left.Children.Add(_check);
        left.Children.Add(_step1);

        left.Children.Add(Head("2. Where it goes"));
        left.Children.Add(_placeText);
        var pos = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pos.Children.Add(Text("Centre X", 11, Muted)); pos.Children.Add(_centreX);
        pos.Children.Add(Text("Y", 11, Muted)); pos.Children.Add(_centreY);
        pos.Children.Add(_centre);
        left.Children.Add(pos);
        var cam = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        cam.Children.Add(_photoBtn); cam.Children.Add(_calibrate);
        left.Children.Add(cam);
        left.Children.Add(_marks);
        left.Children.Add(_blind);

        left.Children.Add(Head("3. Send"));
        left.Children.Add(Text($"Energy ceiling {study.EnergyCeilingJPerMm2:0} J/mm²: the job is read back and refused if any square millimetre would get more.", 11, Muted));
        left.Children.Add(_send);
        left.Children.Add(_auditText);
        left.Children.Add(_step2);

        left.Children.Add(Head("4. Start and follow"));
        left.Children.Add(_step3);
        left.Children.Add(_confirm);
        var run = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        run.Children.Add(_startHere); run.Children.Add(_stop);
        left.Children.Add(run);
        left.Children.Add(_step4);

        var bottom = new DockPanel { Margin = new Thickness(14, 8) };
        var bb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bb.Children.Add(_saveJob); bb.Children.Add(_close);
        DockPanel.SetDock(bb, Dock.Right);
        bottom.Children.Add(bb);
        bottom.Children.Add(_status);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = left, Background = Panel });
        Grid.SetColumn(_photo, 1);
        grid.Children.Add(_photo);
        Grid.SetRow(bottom, 1); Grid.SetColumnSpan(bottom, 2);
        grid.Children.Add(bottom);
        Content = grid;

        _photo.EmptyText = "The laser camera's picture appears here, with the coupon drawn where it will be engraved.";
        _photo.DrawOverlay = DrawJob;
        _photo.TryStartDrag = StartDrag;
        _photo.DragMoved = MoveJob;
        _photo.DragEnded = MoveJob;
        _photo.CursorAt = p => OverJob(p) ? StandardCursorType.SizeAll : StandardCursorType.Arrow;

        _check.Click += async (_, _) => await CheckAsync();
        _photoBtn.Click += async (_, _) => await PhotoAsync();
        _calibrate.Click += async (_, _) => { var saved = await CameraCalibrationWindow.ShowAsync(this, _settings); _settings = LumosSettings.Load(); ShowPlacementState(); if (saved) await PhotoAsync(); };
        _centre.Click += (_, _) => SetCentre(MakeItJob.HomeX, MakeItJob.HomeY);
        _centreX.ValueChanged += (_, _) => { if (!_syncing && _centreX.Value is { } v) SetCentre((double)v, _cy); };
        _centreY.ValueChanged += (_, _) => { if (!_syncing && _centreY.Value is { } v) SetCentre(_cx, (double)v); };
        _blind.IsCheckedChanged += (_, _) => UpdateSendState();
        _marks.IsCheckedChanged += (_, _) => _photo.Redraw();
        _send.Click += async (_, _) => await SendAsync();
        _confirm.IsCheckedChanged += (_, _) => _startHere.IsEnabled = _confirm.IsChecked == true && _phase == Phase.Armed;
        _startHere.Click += async (_, _) => await StartHereAsync();
        _stop.Click += async (_, _) => await StopAsync();
        _saveJob.Click += async (_, _) => await SaveJobAsync();
        _close.Click += (_, _) => Close(_phase == Phase.Finished);
        Closed += (_, _) => _cts.Cancel();

        SetCentre(coupon.BedCentreX ?? MakeItJob.HomeX, coupon.BedCentreY ?? MakeItJob.HomeY);
        ShowPlacementState();
    }

    private LumosUltraClient Client()
    {
        var address = (_address.Text ?? "").Trim();
        if (address.Length == 0) address = LumosUltraClient.DefaultUsbBaseAddress;
        if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase)) address = "http://" + address;
        if (address != _settings.Address) { _settings.Address = address; _settings.Save(); }
        return new LumosUltraClient(address);
    }

    private static void Show(TextBlock t, IBrush brush, string glyph, string text) { t.Text = $"{glyph} {text}"; t.Foreground = brush; }

    // ---------------------------------------------------------------- 1. laser

    private async Task<bool> CheckAsync()
    {
        _check.IsEnabled = false;
        Show(_step1, Busy, "…", "Looking for the laser…");
        try
        {
            using var client = Client();
            if (!await client.IsReachableAsync(_cts.Token))
            {
                Show(_step1, Bad, "✕", $"No answer at {_address.Text}. Is the laser on and connected?");
                return false;
            }
            var info = await client.GetDeviceInfoAsync(_cts.Token);
            var door = await client.TryIsDoorClosedAsync(_cts.Token);
            string who = info is null ? "Laser found" : $"{info.Name ?? "Laser"} ({info.DeviceType}, {info.Power}, firmware {info.McuVersion1 ?? info.AppVersion})";
            if (door != true)
            {
                Show(_step1, Bad, "✕", $"{who}. The door is {(door == false ? "open" : "not confirmed closed")}: close it and check again.");
                return false;
            }
            Show(_step1, Ok, "✓", $"{who}. Door closed.");
            return true;
        }
        catch (Exception ex)
        {
            Show(_step1, Bad, "✕", $"Could not reach the laser: {ex.Message}");
            return false;
        }
        finally { _check.IsEnabled = _phase == Phase.Ready; }
    }

    // ---------------------------------------------------------------- 2. placement

    private (double MinX, double MinY, double MaxX, double MaxY) Extents(double cx, double cy) => CouponJob.Extents(_study, cx, cy);

    private void SetCentre(double cx, double cy)
    {
        if (_phase != Phase.Ready) return;
        double hw = _study.Coupon.BoxWidthMm / 2, hh = _study.Coupon.BoxHeightMm / 2;
        _cx = Math.Clamp(cx, 2 + hw, MakeItJob.BedMm - 2 - hw);
        _cy = Math.Clamp(cy, 2 + hh, MakeItJob.BedMm - 2 - hh);
        _syncing = true;
        _centreX.Value = (decimal)Math.Round(_cx, 1);
        _centreY.Value = (decimal)Math.Round(_cy, 1);
        _syncing = false;
        _photo.Redraw();
    }

    private bool OverJob(PointD p)
    {
        if (_settings.CurrentCamera is not { } cam || _phase != Phase.Ready) return false;
        var b = cam.ToBed(p);
        var (x0, y0, x1, y1) = Extents(_cx, _cy);
        return b.X >= x0 - 2 && b.X <= x1 + 2 && b.Y >= y0 - 2 && b.Y <= y1 + 2;
    }

    private bool StartDrag(PointD p)
    {
        if (!OverJob(p)) return false;
        _dragStartBed = _settings.CurrentCamera!.ToBed(p);
        _dragStartCentre = (_cx, _cy);
        return true;
    }

    private void MoveJob(PointD p)
    {
        if (_settings.CurrentCamera is not { } cam) return;
        var d = cam.ToBed(p) - _dragStartBed;
        SetCentre(_dragStartCentre.x + d.X, _dragStartCentre.y + d.Y);
    }

    /// <summary>The coupon on the picture: its outline, every patch, the ID square and the crosses.</summary>
    private void DrawJob(Canvas canvas, Func<PointD, Point> toScreen, double scale)
    {
        if (_settings.CurrentCamera is not { } cam) return;
        var colour = _phase == Phase.Ready ? Color.Parse("#4FD1FF") : Color.Parse("#E3A640");
        var stroke = new SolidColorBrush(colour);
        var l = _study.Layout;
        double ox = _cx - _study.Coupon.BoxWidthMm / 2, oy = _cy - _study.Coupon.BoxHeightMm / 2;

        IEnumerable<Point> Edge(double x0, double y0, double x1, double y1, int n) =>
            Enumerable.Range(0, n + 1).Select(i => toScreen(cam.ToImage(new PointD(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n))));
        Polygon Box(double x0, double y0, double x1, double y1, int n, IBrush? fill, double thick) => new()
        {
            Points = Edge(x0, y0, x1, y0, n).Concat(Edge(x1, y0, x1, y1, n)).Concat(Edge(x1, y1, x0, y1, n)).Concat(Edge(x0, y1, x0, y0, n)).ToList(),
            Stroke = stroke, StrokeThickness = thick, Fill = fill,
        };

        var (ex0, ey0, ex1, ey1) = Extents(_cx, _cy);
        if (_study.Coupon.Shape == CouponShape.Coin)
        {
            double r = _study.Coupon.DiameterMm / 2;
            var pts = Enumerable.Range(0, 48).Select(i => { double a = i * Math.PI * 2 / 48; return toScreen(cam.ToImage(new PointD(_cx + r * Math.Cos(a), _cy + r * Math.Sin(a)))); }).ToList();
            canvas.Children.Add(new Polygon { Points = pts, Stroke = stroke, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(25, colour.R, colour.G, colour.B)), StrokeDashArray = [5, 3] });
        }
        else
        {
            var outline = Box(ex0, ey0, ex1, ey1, 12, new SolidColorBrush(Color.FromArgb(25, colour.R, colour.G, colour.B)), 2);
            outline.StrokeDashArray = [5, 3];
            canvas.Children.Add(outline);
        }
        var cellFill = new SolidColorBrush(Color.FromArgb(70, colour.R, colour.G, colour.B));
        var idFill = new SolidColorBrush(Color.FromArgb(140, colour.R, colour.G, colour.B));
        for (int i = 0; i < l.Cells.Count; i++)
        {
            var c = l.Cells[i];
            double x = ox + c.X, y = oy + c.Y;
            bool used = i == l.Capacity || _coupon.Patches.Any(p => p.Index == i);
            canvas.Children.Add(Box(x, y, x + l.PatchSizeMm, y + l.PatchSizeMm, 1, i == l.Capacity ? idFill : used ? cellFill : null, 1));
        }
        if (_marks.IsChecked == true)
            foreach (var (mx, my) in CouponJob.CrossCentres(_study, ox, oy))
            {
                double half = CouponJob.CrossMm / 2;
                canvas.Children.Add(new Line { StartPoint = toScreen(cam.ToImage(new PointD(mx - half, my))), EndPoint = toScreen(cam.ToImage(new PointD(mx + half, my))), Stroke = stroke, StrokeThickness = 1.5 });
                canvas.Children.Add(new Line { StartPoint = toScreen(cam.ToImage(new PointD(mx, my - half))), EndPoint = toScreen(cam.ToImage(new PointD(mx, my + half))), Stroke = stroke, StrokeThickness = 1.5 });
            }
        var label = new TextBlock
        {
            Text = _phase == Phase.Ready ? $"DV{_coupon.Number} will engrave here · drag to move" : $"DV{_coupon.Number} engraves here",
            Foreground = stroke, FontSize = 12, FontWeight = FontWeight.SemiBold,
        };
        var at = toScreen(cam.ToImage(new PointD(ex0, ey1 + 1)));
        Canvas.SetLeft(label, at.X);
        Canvas.SetTop(label, at.Y + 4);
        canvas.Children.Add(label);
    }

    private void ShowPlacementState()
    {
        var cam = _settings.CurrentCamera;
        _placeText.Text = cam is not null
            ? $"Camera calibrated {cam.Calibrated.ToLocalTime():d MMM yyyy} (within {cam.MaxMm:0.0} mm), only true at the height it was calibrated at. Put the blank on the bed and focus as usual, take a picture, drag the coupon onto the blank, take a fresh picture to check, then send."
            : _settings.Camera is not null
                ? "The camera calibration is out of date (made with the picture turned another way, or an older target): calibrate again."
                : "Until the laser's camera is calibrated, DepthView cannot show where the coupon lands. Calibrate once: it engraves nine small crosses, so cover most of the bed with a big scrap (cardboard is fine).";
        UpdateSendState();
    }

    private void UpdateSendState()
    {
        if (_phase != Phase.Ready) return;
        _send.IsEnabled = _placementSeen || _blind.IsChecked == true;
        _status.Text = _send.IsEnabled ? "" : "Not sent yet: first see where it will engrave (take a picture), or tick that you have checked the position yourself.";
    }

    private async Task PhotoAsync()
    {
        _photoBtn.IsEnabled = false;
        _status.Text = "Taking a picture with the laser's camera…";
        try
        {
            _settings = LumosSettings.Load();
            using var client = Client();
            var bytes = await client.TakePhotoAsync(_cts.Token);
            var img = await Task.Run(() => _settings.Upright(bytes));
            _photo.SetPicture(img, keepView: true);
            _placementSeen = _settings.CurrentCamera is not null;
            ShowPlacementState();
            _status.Text = _placementSeen ? "Picture taken. Drag the coupon onto the blank." : "Picture taken, but without a calibration it cannot show where the coupon lands.";
            if (!_zoomed && _settings.CurrentCamera is { } cam)
            {
                _zoomed = true;
                var (x0, y0, x1, y1) = Extents(_cx, _cy);
                var pts = new[] { new PointD(x0, y0), new PointD(x1, y0), new PointD(x0, y1), new PointD(x1, y1) }.Select(cam.ToImage).ToArray();
                double w = Math.Max(pts.Max(p => p.X) - pts.Min(p => p.X), img.Width * 0.25), h = Math.Max(pts.Max(p => p.Y) - pts.Min(p => p.Y), img.Height * 0.25);
                double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
                _photo.ZoomTo(mx - w, my - h, mx + w, my + h);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = $"Could not take a picture: {ex.Message}"; }
        finally { _photoBtn.IsEnabled = true; }
    }

    // ---------------------------------------------------------------- 3. audit and send

    private string BuildJob() => CouponJob.Gcode(_study, _coupon, _cx, _cy, _marks.IsChecked == true);

    private JobAudit.Result? AuditJob(string gcode)
    {
        var hot = CouponJob.TooHot(_study, _coupon.Patches);
        if (hot.Count > 0)
        {
            Show(_step2, Bad, "✕", $"Not sent: {hot.Count} patch(es) over the ceiling of {_study.EnergyCeilingJPerMm2:0} J/mm² (hottest {hot[0].Label} at {hot[0].JPerMm2:0}). Plan the study with fewer layers or less power.");
            return null;
        }
        var audit = CouponJob.Audit(_study, gcode);
        _auditText.Text = audit.Message;
        if (!audit.Ok) { Show(_step2, Bad, "✕", audit.Message); return null; }
        return audit;
    }

    private async Task SendAsync()
    {
        if (_phase != Phase.Ready) return;
        if (!_placementSeen && _blind.IsChecked != true) { UpdateSendState(); return; }
        _send.IsEnabled = false;
        if (!await CheckAsync()) { _send.IsEnabled = true; return; }
        string gcode = BuildJob();
        if (AuditJob(gcode) is null) return;   // never re-enabled: this job must not be sent
        _gcode = gcode;
        string? kept = AblationDatabase.KeepJob(_study, _coupon, gcode);
        Show(_step2, Busy, "…", $"Sending the job ({gcode.Length / 1024.0:0} KB)…");
        try
        {
            using var client = Client();
            await client.UploadAndArmJobAsync(gcode, ct: _cts.Token);
        }
        catch (Exception ex)
        {
            Show(_step2, Bad, "✕", $"Could not send the job: {ex.Message}");
            _send.IsEnabled = true;
            return;
        }
        _phase = Phase.Armed;
        _marks.IsEnabled = _centreX.IsEnabled = _centreY.IsEnabled = _centre.IsEnabled = _check.IsEnabled = false;
        _photo.Redraw();
        Show(_step2, Ok, "✓", $"Sent and armed, centred at X {_cx:0.0}, Y {_cy:0.0} mm." + (kept is null ? "" : $" Kept as {System.IO.Path.GetFileName(kept)}."));
        Show(_step3, Busy, "▶", "Press Start on the machine, or tick the confirmation and start from here. Nothing fires until you do.");
        _startHere.IsEnabled = _confirm.IsChecked == true;
        _status.Text = "Armed and waiting for Start.";
        AblationDatabase.MarkSent(_coupon, _cx, _cy, kept);
        try { _db.Save(); } catch { }
        _ = FollowAsync();
    }

    private async Task SaveJobAsync()
    {
        string gcode = BuildJob();
        var audit = AuditJob(gcode);
        if (audit is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the coupon job", SuggestedFileName = $"DV{_coupon.Number}.gc",
            FileTypeChoices = [new FilePickerFileType("Lumos Ultra job") { Patterns = ["*.gc"] }],
        });
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path.LocalPath, gcode);
        _status.Text = $"Job written to {file.Name} ({audit.Message})";
    }

    private async Task StartHereAsync()
    {
        if (_phase != Phase.Armed || _confirm.IsChecked != true) return;
        _startHere.IsEnabled = false;
        try
        {
            using var client = Client();
            await client.StartArmedJobAsync(_cts.Token);
            _status.Text = "Start sent.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start the job: {ex.Message}. The machine's Start button still works.";
            _startHere.IsEnabled = _confirm.IsChecked == true;
        }
    }

    private async Task StopAsync()
    {
        try
        {
            using var client = Client();
            await client.CancelJobAsync(_cts.Token);
            _status.Text = "Stop sent. If the laser keeps going, use the stop button on the machine.";
        }
        catch (Exception ex) { _status.Text = $"Could not send stop: {ex.Message}. Use the stop button on the machine."; }
    }

    // ---------------------------------------------------------------- 4. follow

    private async Task FollowAsync()
    {
        int notBusy = 0, errors = 0;
        using var client = Client();
        while (!_cts.IsCancellationRequested && _phase is Phase.Armed or Phase.Running)
        {
            try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
            JobStatus status;
            try { status = await client.GetJobStatusAsync(_cts.Token); errors = 0; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (++errors == 5) _status.Text = $"Lost touch with the laser ({ex.Message}). Still trying…";
                continue;
            }
            if (_phase == Phase.Armed && status == JobStatus.Busy)
            {
                _phase = Phase.Running;
                _runStarted = DateTime.Now;
                _startHere.IsEnabled = false;
                _stop.IsVisible = true;
                Show(_step3, Ok, "✓", $"Started at {_runStarted:HH:mm:ss}.");
            }
            if (_phase == Phase.Running)
            {
                var t = DateTime.Now - _runStarted;
                if (status == JobStatus.Busy)
                {
                    notBusy = 0;
                    Show(_step4, Busy, "…", $"Cutting… {Durations.Clock(t)}");
                }
                else if (++notBusy >= 2)
                {
                    _phase = Phase.Finished;
                    _stop.IsVisible = false;
                    AblationDatabase.MarkEngraved(_coupon);
                    try { _db.Save(); } catch { }
                    Show(_step4, Ok, "✓", $"Finished in {Durations.Clock(t)}. Coupon DV{_coupon.Number} is recorded as engraved; measure it when it has cooled.");
                    _status.Text = "Done.";
                }
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Control Row(string label, Control editor, string tip)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
        var t = Text(label, 12, Ink); t.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(t, tip); ToolTip.SetTip(editor, tip);
        g.Children.Add(t); Grid.SetColumn(editor, 1); g.Children.Add(editor);
        return g;
    }

    private static NumericUpDown Num() => new() { Minimum = 0, Maximum = 210, Increment = 0.5m, FormatString = "0.0", ShowButtonSpinner = false, Width = 90 };
    private static TextBlock Head(string s) => new() { Text = s, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = B("#9FB4FF"), Margin = new Thickness(0, 10, 0, 2) };
    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new() { Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"), BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        ToolTip.SetTip(b, tip);
        return b;
    }
}
