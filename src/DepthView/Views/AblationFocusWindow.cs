using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Ablation;
using LumosUltra.Client;
using LumosUltra.Client.Models;
using LumosUltra.Desktop;
using LumosUltra.Jobs;

namespace DepthView.Views;

/// <summary>
/// Guides the user to an accurate focus before a coupon is cut - the first step of any test.
/// Three parts: focus the machine as for any job; read the machine's Z (or type what MakeIt
/// shows); optionally cut a focus ramp - a row of numbered bars at Z offsets around that
/// focus - and pick the sharpest bar. The ramp is also the proof that the Z number drives
/// the head the way the jobs assume: a focus is marked verified only after one.
/// Returns the <see cref="FocusRecord"/>, or null.
/// </summary>
public sealed class AblationFocusWindow : Window
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Ok = B("#5CC28A"), Busy = B("#E3A640"), Bad = B("#EF6A70");

    private readonly Study _study;
    private readonly string _address;
    private readonly double _cx, _cy;
    private readonly CancellationTokenSource _cts = new();
    private bool _rampSent, _rampDone;
    private string? _rampFile;

    private readonly TextBox _method = new() { Text = "machine autofocus" };
    private readonly NumericUpDown _z = new() { Minimum = 0, Maximum = 300, Increment = 0.01m, FormatString = "0.00", ShowButtonSpinner = false, Width = 110 };
    private readonly NumericUpDown _range = new() { Minimum = 0.1m, Maximum = (decimal)FocusRamp.MaxRangeMm, Increment = 0.1m, Value = 1.5m, FormatString = "0.00", ShowButtonSpinner = false, Width = 90 };
    private readonly NumericUpDown _step = new() { Minimum = (decimal)FocusRamp.MinStepMm, Maximum = 1, Increment = 0.05m, Value = 0.25m, FormatString = "0.00", ShowButtonSpinner = false, Width = 90 };
    private readonly NumericUpDown _power = new() { Minimum = 1, Maximum = 100, Increment = 5, Value = 50, FormatString = "0", ShowButtonSpinner = false, Width = 70 };
    private readonly NumericUpDown _speed = new() { Minimum = 10, Maximum = 5000, Increment = 100, Value = 1000, FormatString = "0", ShowButtonSpinner = false, Width = 90 };
    private readonly NumericUpDown _bar = new() { Minimum = 1, Maximum = 13, Increment = 1, FormatString = "0", Width = 70 };
    private readonly CheckBox _clear = new() { Content = "" };
    private readonly CheckBox _changed = new() { Content = "The bars visibly differ from one another (the Z moves did what the job said)" };
    private readonly TextBox _notes = new() { AcceptsReturn = true, Height = 50, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _readText = Text("", 11, Muted), _rampText = Text("", 12, Ink), _rampPlan = Text("", 11, Muted),
        _pick = Text("", 12, Ink), _status = Text("", 12, Bright), _previous = Text("", 11, Muted);
    private readonly Button _read, _sendRamp, _stop, _record, _cancel;

    public AblationFocusWindow(Study study, string address, double couponCentreX, double couponCentreY, FocusRecord? previous)
    {
        _study = study; _address = address; _cx = couponCentreX; _cy = couponCentreY;
        Title = $"Focus - {study.Name}";
        Width = 760; Height = 820; MinWidth = 600; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        _read = MakeButton("Read Z from the laser", "Ask the laser for its height at the coupon's centre (its camera's distance measurement) and put the number here.");
        _sendRamp = MakeButton("Audit, send and arm the ramp", "Build the ramp job, check it, send it and arm it. Press Start on the machine to cut it.");
        _stop = MakeButton("Stop", "Cancel the ramp job.");
        _record = MakeButton("Record this focus", "Keep this focus for the coupon and for the next coupons of the same lens and thickness.");
        _cancel = MakeButton("Cancel", "Close without recording a focus.");
        _stop.IsVisible = false;

        if (previous is not null) { _method.Text = previous.Method; if (previous.MachineZMm is { } pz) _z.Value = (decimal)pz; }
        _previous.Text = previous is null
            ? "No focus recorded yet for this laser, lens and coupon thickness."
            : $"Last time ({previous.Age(DateTimeOffset.UtcNow)}): {previous.Summary}.";

        var p = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        p.Children.Add(Head("Why focus comes first"));
        p.Children.Add(Text("Removal depends on focus more than on any setting: a few tenths of a millimetre out and every patch on the coupon removes less, and the whole study is biased. " +
                            "The focus is recorded with each coupon, and kept for the next coupons of the same lens and thickness so you can skip this when nothing has changed.", 12, Ink));
        p.Children.Add(_previous);

        p.Children.Add(Head("1. Focus the machine"));
        p.Children.Add(Text($"Put the coupon (or a scrap of exactly {study.Coupon.ThicknessMm:0.##} mm, the same thickness) on the bed where the coupon will be cut, " +
                            "and focus the machine as you would for any job: its own focus routine, or MakeIt's material thickness. Leave it there.", 12, Ink));
        p.Children.Add(Row("How", _method, "A few words for the record: machine autofocus, MakeIt thickness 3 mm, by hand with the focus card..."));

        p.Children.Add(Head("2. The machine's Z"));
        p.Children.Add(Text("The jobs can put the head at this height themselves (as a MakeIt relief job does with G0Z). Read it from the laser, or type the height MakeIt shows. " +
                            "Leave it empty if it cannot be known: coupons are then cut at whatever focus the machine is at, and no Z moves are sent.", 11, Muted));
        var zRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        zRow.Children.Add(Text("Machine Z mm", 12, Ink)); zRow.Children.Add(_z); zRow.Children.Add(_read);
        p.Children.Add(zRow);
        p.Children.Add(_readText);

        p.Children.Add(Head("3. Verify with a ramp (the first time for each lens and thickness)"));
        p.Children.Add(Text("A row of numbered bars, each cut at a different Z around the machine's focus: one single line and one small square per bar, with the bar's number below it. " +
                            "The bar whose line is narrowest and whose square is brightest is at focus. Cut it on a scrap of the same thickness, placed where the coupon goes. " +
                            "If no bar looks different from the others, the Z number is not what the jobs assume - do not put Z in the jobs.", 11, Muted));
        var r = new WrapPanel();
        r.Children.Add(Small("± range mm", _range)); r.Children.Add(Small("step mm", _step)); r.Children.Add(Small("power %", _power)); r.Children.Add(Small("speed mm/s", _speed));
        p.Children.Add(r);
        p.Children.Add(_rampPlan);
        p.Children.Add(_clear);
        var send = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        send.Children.Add(_sendRamp); send.Children.Add(_stop);
        p.Children.Add(send);
        p.Children.Add(_rampText);
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pickRow.Children.Add(Text("Sharpest bar", 12, Ink)); pickRow.Children.Add(_bar); pickRow.Children.Add(_pick);
        p.Children.Add(pickRow);
        p.Children.Add(_changed);

        p.Children.Add(Head("4. Record"));
        p.Children.Add(Row("Notes", _notes, "Anything a later reader should know: which scrap, how the machine was focused, doubts."));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8) };
        buttons.Children.Add(_cancel); buttons.Children.Add(_record);
        var bottom = new DockPanel { Margin = new Thickness(0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        _status.Margin = new Thickness(16, 8);
        bottom.Children.Add(_status);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = p, Background = Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(bottom, 1); grid.Children.Add(bottom);
        Content = grid;
        this.FitToScreen();

        _read.Click += async (_, _) => await ReadZAsync();
        _sendRamp.Click += async (_, _) => await SendRampAsync();
        _stop.Click += async (_, _) => await StopAsync();
        _record.Click += (_, _) => Close(Build());
        _cancel.Click += (_, _) => Close(null);
        foreach (var n in new[] { _range, _step, _z }) n.ValueChanged += (_, _) => RefreshPlan();
        _bar.ValueChanged += (_, _) => RefreshPick();
        _changed.IsCheckedChanged += (_, _) => RefreshPick();
        Closed += (_, _) => _cts.Cancel();
        RefreshPlan();
        RefreshPick();
    }

    public static async Task<FocusRecord?> ShowAsync(Window owner, Study study, string address, double cx, double cy, FocusRecord? previous)
        => await new AblationFocusWindow(study, address, cx, cy, previous) { Icon = owner.Icon }.ShowDialog<FocusRecord?>(owner);

    /// <summary>The machine Z as entered; empty or zero means "not known".</summary>
    private double? Z => _z.Value is { } v && v > 0 ? (double)v : null;
    private double Range => _range.Value is { } v ? (double)v : 1.5;
    private double Step => _step.Value is { } v ? (double)v : 0.25;

    private void RefreshPlan()
    {
        var offsets = FocusRamp.Offsets(Range, Step);
        _bar.Maximum = offsets.Length;
        if (!_rampDone) _bar.Value = offsets.Length / 2 + 1;
        if (Z is { } z)
        {
            double lo = z - Range, hi = z + Range;
            _rampPlan.Text = $"{offsets.Length} bars, {FocusRamp.WidthMm(offsets.Length):0} × {FocusRamp.HeightMm:0} mm, centred at X {_cx:0.0}, Y {_cy:0.0}: Z from {lo:0.00} to {hi:0.00} mm, bar {offsets.Length / 2 + 1} at the machine's own focus.";
            _clear.Content = $"The head can move freely between Z {lo:0.00} and {hi:0.00} mm: nothing on the bed is taller than the scrap, and the Z above is the machine's real height";
            _clear.IsEnabled = true;
        }
        else
        {
            _rampPlan.Text = "The ramp needs the machine's Z: without it there is nothing to step around.";
            _clear.Content = "The head can move freely around the machine's Z";
            _clear.IsEnabled = false; _clear.IsChecked = false;
        }
        _sendRamp.IsEnabled = !_rampSent && Z is not null;
        RefreshPick();
    }

    private void RefreshPick()
    {
        var offsets = FocusRamp.Offsets(Range, Step);
        int i = (int)(_bar.Value ?? (offsets.Length / 2 + 1)) - 1;
        _bar.IsEnabled = _changed.IsEnabled = _rampDone;
        if (!_rampDone) { _pick.Text = "(after the ramp has been cut)"; return; }
        if (i < 0 || i >= offsets.Length) { _pick.Text = ""; return; }
        double off = offsets[i];
        _pick.Text = Z is { } z ? $"offset {off:+0.00;-0.00;0.00} mm → focus Z {z + off:0.00} mm" : $"offset {off:+0.00;-0.00;0.00} mm";
    }

    private FocusRecord Build()
    {
        var offsets = FocusRamp.Offsets(Range, Step);
        int i = (int)(_bar.Value ?? (offsets.Length / 2 + 1)) - 1;
        bool verified = _rampDone && _changed.IsChecked == true && Z is not null && i >= 0 && i < offsets.Length;
        return new FocusRecord
        {
            LaserId = _study.Laser.Id, Lens = _study.Laser.Lens, ThicknessMm = _study.Coupon.ThicknessMm,
            Method = (_method.Text ?? "").Trim(), MachineZMm = Z, MachineReply = _lastReply,
            OffsetMm = verified ? offsets[i] : 0, ZVerified = verified,
            RampBars = _rampDone ? offsets.Length : null, RampStepMm = _rampDone ? Step : null, RampJobFile = _rampFile,
            Notes = (_notes.Text ?? "").Trim(),
        };
    }

    private string? _lastReply;

    private LumosUltraClient Client() => new(_address);

    private async Task ReadZAsync()
    {
        _read.IsEnabled = false;
        _readText.Text = "Asking the laser…";
        try
        {
            using var client = Client();
            string q = string.Create(CultureInfo.InvariantCulture, $"/camera/measure_distance?x={_cx:0.##}&y={_cy:0.##}");
            string raw = await client.GetRawAsync(q, _cts.Token);
            _lastReply = raw.Length > 400 ? raw[..400] : raw;
            var h = MachineHeight.Parse(raw);
            if (h.BestZ is { } z)
            {
                _z.Value = (decimal)Math.Round(z, 3);
                _readText.Text = $"The laser answered {_lastReply.Trim()} - taken as Z {z:0.00} mm. Before trusting it, compare it with the G0Z at the start of a MakeIt relief job made at this same focus; the ramp will show whether it drives the head.";
            }
            else
                _readText.Text = $"The laser answered, but no height could be read from it: {_lastReply.Trim()}. Type the Z MakeIt shows instead.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _readText.Text = $"Could not read the height: {ex.Message}. Type the Z MakeIt shows instead, or leave it empty."; }
        finally { _read.IsEnabled = true; RefreshPlan(); }
    }

    private async Task SendRampAsync()
    {
        if (Z is not { } z) return;
        if (_clear.IsChecked != true) { _status.Text = "Tick that the head can move freely over the Z band first."; return; }
        _sendRamp.IsEnabled = false;
        double power = (double)(_power.Value ?? 50), speed = (double)(_speed.Value ?? 1000);
        string gcode;
        try { gcode = FocusRamp.Gcode(_study, z, Range, Step, _cx, _cy, power, speed); }
        catch (Exception ex) { Show(_rampText, Bad, "✕", $"Could not build the ramp: {ex.Message}"); _sendRamp.IsEnabled = true; return; }
        if (FocusRamp.ZAudit(gcode, z - Range, z + Range) is { } zbad) { Show(_rampText, Bad, "✕", zbad); return; }
        var audit = CouponJob.Audit(_study, gcode);
        if (!audit.Ok) { Show(_rampText, Bad, "✕", audit.Message); return; }
        Show(_rampText, Busy, "…", "Checking the laser…");
        try
        {
            using var client = Client();
            if (!await client.IsReachableAsync(_cts.Token)) { Show(_rampText, Bad, "✕", $"No answer at {_address}."); _sendRamp.IsEnabled = true; return; }
            if (await client.TryIsDoorClosedAsync(_cts.Token) != true) { Show(_rampText, Bad, "✕", "The door is not confirmed closed."); _sendRamp.IsEnabled = true; return; }
            _rampFile = AblationDatabase.KeepJob("focus ramp", _study, gcode);
            Show(_rampText, Busy, "…", $"Sending the ramp ({gcode.Length / 1024.0:0} KB, {audit.Message})…");
            await client.UploadAndArmJobAsync(gcode, ct: _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Show(_rampText, Bad, "✕", $"Could not send the ramp: {ex.Message}"); _sendRamp.IsEnabled = true; return; }
        _rampSent = true;
        _z.IsEnabled = _range.IsEnabled = _step.IsEnabled = _read.IsEnabled = false;
        Show(_rampText, Busy, "▶", "Sent and armed. Press Start on the machine; the bars are numbered 1 (lowest Z) to the highest.");
        _stop.IsVisible = true;
        _ = FollowAsync();
    }

    private async Task StopAsync()
    {
        try { using var client = Client(); await client.CancelJobAsync(_cts.Token); _status.Text = "Stop sent."; }
        catch (Exception ex) { _status.Text = $"Could not send stop: {ex.Message}. Use the stop button on the machine."; }
    }

    private async Task FollowAsync()
    {
        bool running = false; int notBusy = 0, errors = 0; var started = DateTime.Now;
        using var client = Client();
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
            JobStatus status;
            try { status = await client.GetJobStatusAsync(_cts.Token); errors = 0; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (++errors == 5) _status.Text = $"Lost touch with the laser ({ex.Message}). Still trying…"; continue; }
            if (!running && status == JobStatus.Busy) { running = true; started = DateTime.Now; Show(_rampText, Busy, "…", "Cutting the ramp…"); }
            else if (running && status == JobStatus.Busy) { notBusy = 0; Show(_rampText, Busy, "…", $"Cutting the ramp… {Durations.Clock(DateTime.Now - started)}"); }
            else if (running && ++notBusy >= 2)
            {
                _rampDone = true;
                _stop.IsVisible = false;
                Show(_rampText, Ok, "✓", "Ramp cut. Look at it under magnification: which bar's single line is narrowest and square brightest? Enter its number, and tick whether the bars differed at all.");
                var offsets = FocusRamp.Offsets(Range, Step);
                _bar.Value = offsets.Length / 2 + 1;
                RefreshPick();
                return;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void Show(TextBlock t, IBrush brush, string glyph, string text) { t.Text = $"{glyph} {text}"; t.Foreground = brush; }

    private static Control Row(string label, Control editor, string tip)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var t = Text(label, 12, Ink); t.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(t, tip); ToolTip.SetTip(editor, tip);
        g.Children.Add(t); Grid.SetColumn(editor, 1); g.Children.Add(editor);
        return g;
    }

    private static Control Small(string label, Control editor)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 12, 4) };
        s.Children.Add(Text(label, 11, Muted)); s.Children.Add(editor);
        return s;
    }

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
