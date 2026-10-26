using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DepthView.Ablation;
using DepthView.Controls;

namespace DepthView.Views;

/// <summary>
/// The ablation study: studies on the left, the chosen study's design and coupons on the
/// right, the coupon drawn to scale underneath. From here a study is created (with the
/// projection of how many identical coupons it needs), coupons are planned one at a time and
/// get their test numbers, each is cut from <see cref="AblationSendWindow"/>, a coupon can be
/// removed (retired) to be cut again, studies are shared as files and carried between the
/// 60 W and 100 W ratings, and the database can be reset for a test run.
///
/// The measure phase is a stub: its data shape is settled (<see cref="PatchMeasurement"/>)
/// and the entry window waits for the depth gauge and its interface (TODO §7.7b).
/// </summary>
public sealed class AblationWindow : Window
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"),
        Muted = B("#8A929E"), Amber = B("#F0C674"), Red = B("#EF6A70"), Green = B("#5CC28A");

    private readonly AblationDatabase _db;
    private readonly ListBox _studies = new(), _coupons = new();
    private readonly TextBlock _summary = Text("", 12, Ink), _designText = Text("", 12, Ink), _hover = Text("", 11, Muted), _status = Text("", 11, Muted);
    private readonly CouponPreview _preview = new() { MinHeight = 320 };
    private readonly Button _newStudy, _import, _export, _convert, _deleteStudy, _reset, _plan, _cut, _measure, _remove, _jobs, _saveJob, _seeds;

    public AblationWindow() : this(AblationDatabase.Load()) { }

    /// <summary>On a given database - the demo one for screenshots and checks.</summary>
    public AblationWindow(AblationDatabase db)
    {
        _db = db;
        Title = "Ablation study";
        Width = 1320; Height = 880; MinWidth = 1040; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        _newStudy = MakeButton("New study...", "Material, laser, coupon size and the settings to test, with a projection of how many identical coupons it needs.");
        _import = MakeButton("Import...", "Bring in a study someone shared as a file. It keeps its own test numbers and is marked with where it came from. A measured study from the same laser model and material is the next best evidence after your own coupons: its measurements count as grade A from another bench, above every bundled reference study.");
        _export = MakeButton("Export...", "Write the chosen study - design, coupons, measurements - as a file to share.");
        _convert = MakeButton("Carry to another rating...", "A copy of the study's design for the other MOPA rating (60 W or 100 W), every setting matched by watts. Predictions until cut and measured.");
        _deleteStudy = MakeButton("Delete study", "Remove the chosen study and its coupons from the database. Its test numbers are not reused.");
        _reset = MakeButton("Reset database...", "Start over for a test run: the current file is kept beside the new one, and numbering restarts at 1.");
        _seeds = MakeButton("Reference studies...", "Add the reference studies bundled with DepthView - figures other people reported, with their source and grade on every one. Nothing in them was cut here.");
        _plan = MakeButton("Plan the next coupon", "Fill the next coupon with the next settings in the design and give it its test number.");
        _cut = MakeButton("Cut this coupon...", "Place it on the laser camera's picture, audit the job, send, arm and start.");
        _measure = MakeButton("Measure...", "Enter the depth readings for this coupon's patches. Waiting for the gauge: not built yet.");
        _remove = MakeButton("Remove (retire) coupon...", "The coupon failed or must be cut again: its number is retired and its settings go back to the pool. The re-cut gets a new number.");
        _jobs = MakeButton("Open the jobs folder", "Where every job file sent is kept, named by test number.");
        _saveJob = MakeButton("Save job file...", "Write this coupon's audited G-code to a file without a laser (for a look, or to send another way).");

        var left = new DockPanel { Margin = new Thickness(12) };
        var leftButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { _newStudy, _import, _export, _convert, _seeds, _deleteStudy, _reset }) { b.Margin = new Thickness(0, 0, 6, 6); leftButtons.Children.Add(b); }
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        var lh = Head("Studies"); DockPanel.SetDock(lh, Dock.Top); left.Children.Add(lh);
        left.Children.Add(_studies);

        var right = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel { Spacing = 6 };
        var preview = new Border
        {
            Background = B("#2A2410"), BorderBrush = B("#E3A640"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 4),
            Child = new TextBlock
            {
                Text = "Preview: the ablation study is framed in, not yet functional. Nothing here has been run against a laser yet - the first coupons and the measuring step are still to come. Look around, but do not plan a material study on it yet.",
                FontSize = 12, Foreground = B("#F0D28A"), TextWrapping = TextWrapping.Wrap,
            },
        };
        ToolTip.SetTip(preview, "This notice goes away once the study has cut and measured its first coupons.");
        top.Children.Add(preview);
        top.Children.Add(Head("Study"));
        top.Children.Add(_summary);
        top.Children.Add(_designText);
        top.Children.Add(Head("Coupons"));
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        var couponButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var b in new[] { _plan, _cut, _saveJob, _measure, _remove, _jobs }) { b.Margin = new Thickness(0, 0, 6, 6); couponButtons.Children.Add(b); }
        DockPanel.SetDock(couponButtons, Dock.Top);
        right.Children.Add(couponButtons);
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        split.Children.Add(_coupons);
        var pv = new DockPanel();
        DockPanel.SetDock(_hover, Dock.Bottom);
        pv.Children.Add(_hover);
        pv.Children.Add(_preview);
        Grid.SetColumn(pv, 1);
        split.Children.Add(pv);
        right.Children.Add(split);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        var lp = new Border { Background = Panel, Child = left };
        grid.Children.Add(lp);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _status.Margin = new Thickness(12, 4);
        Grid.SetRow(_status, 1); Grid.SetColumnSpan(_status, 2);
        grid.Children.Add(_status);
        Content = grid;
        this.FitToScreen();

        _preview.HoverText = t => _hover.Text = t;
        _studies.SelectionChanged += (_, _) => ShowStudy();
        _coupons.SelectionChanged += (_, _) => ShowCoupon();
        _newStudy.Click += async (_, _) => await NewStudyAsync();
        _import.Click += async (_, _) => await ImportAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _convert.Click += async (_, _) => await ConvertAsync();
        _deleteStudy.Click += async (_, _) => await DeleteStudyAsync();
        _reset.Click += async (_, _) => await ResetAsync();
        _plan.Click += (_, _) => PlanCoupon();
        _cut.Click += async (_, _) => await CutAsync();
        _saveJob.Click += async (_, _) => await SaveJobAsync();
        _measure.Click += async (_, _) => await MessageAsync("Measure", "The measure phase is not built yet. Its record is settled - readings in micrometres relative to the untouched gap beside each patch, the method, notes - and the entry window waits for the depth gauge and its interface, so it is shaped by real numbers rather than guessed.");
        _remove.Click += async (_, _) => await RemoveAsync();
        _jobs.Click += (_, _) => { try { Directory.CreateDirectory(AblationDatabase.JobsFolder); Process.Start(new ProcessStartInfo(AblationDatabase.JobsFolder) { UseShellExecute = true }); } catch (Exception ex) { _status.Text = ex.Message; } };

        _seeds.Click += async (_, _) => await OfferSeedsAsync(unasked: false);

        FillStudies();
        _status.Text = $"Database: {_db.Path} - next test number DV{_db.NextTestNumber}.";

        // An empty database is offered the bundled reference studies once per seed set - with
        // their sources in front of the person before anything is added. Not on the demo
        // database or a screenshot run, which have their own contents.
        Opened += async (_, _) =>
        {
            if (Program.StartupAblation || _db.Studies.Count > 0 || !_db.ShouldOfferSeeds) return;
            await OfferSeedsAsync(unasked: true);
        };
    }

    /// <summary>The dialog this window last opened over itself (the seed offer), for the screenshot path.</summary>
    public Window? CurrentDialog { get; private set; }

    /// <summary>Selects the last study in the list (a reference study on the demo database), for the screenshot path.</summary>
    public void SelectLastStudy() { if (_studies.Items.Count > 0) _studies.SelectedIndex = _studies.Items.Count - 1; }

    private Study? Study => (_studies.SelectedItem as ListBoxItem)?.Tag as Study;
    private Coupon? Coupon => (_coupons.SelectedItem as ListBoxItem)?.Tag as Coupon;

    private void FillStudies(Study? select = null)
    {
        _studies.Items.Clear();
        foreach (var s in _db.Studies)
        {
            string tag = s.Reference ? $"\nREFERENCE - {s.Origin}; nothing here was cut on this machine" : s.Origin == "local" ? "" : $"\nimported: {s.Origin}";
            var item = new ListBoxItem { Tag = s, Content = new TextBlock { Text = $"{s.Name}\n{s.Material.Summary} - {s.Laser.RatedPowerW:0} W {s.Laser.Kind}{tag}", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = s.Reference ? Muted : Ink } };
            _studies.Items.Add(item);
            if (ReferenceEquals(s, select)) _studies.SelectedItem = item;
        }
        if (_studies.SelectedItem is null && _studies.Items.Count > 0) _studies.SelectedIndex = 0;
        ShowStudy();
    }

    private void ShowStudy()
    {
        var s = Study;
        bool has = s is not null;
        foreach (var b in new[] { _export, _convert, _deleteStudy, _plan }) b.IsEnabled = has;
        _convert.IsEnabled = has && s!.Laser.Kind == LaserKind.Mopa;
        _coupons.Items.Clear();
        if (s is null)
        {
            _summary.Text = "No study yet. Create one: it asks for the material and alloy, the laser and its rating, the coupon every sheet or blank must match, and the settings to test, and says how many coupons that needs.";
            _designText.Text = "";
            _preview.Show(null, null);
            ShowCoupon();
            return;
        }
        var l = s.Layout;
        if (s.Reference)
        {
            var provs = s.Coupons.SelectMany(c => c.Patches).Select(p => p.Provenance?.Summary).Where(x => x is not null).Distinct().ToList();
            _summary.Text = $"REFERENCE STUDY - {s.Origin}. Nothing in it was cut or measured on this machine: every figure is what its source reported, graded as the source deserves" +
                            (provs.Count > 0 ? $" ({string.Join("; ", provs)})" : "") + ". It cannot be planned, cut or measured; it can be exported, carried to another rating, or deleted.\n" +
                            $"{s.Material.Summary} on {s.Laser.Summary}." + (s.Notes.Length > 0 ? $"\n{s.Notes}" : "");
            _designText.Text = $"{s.Design.Count} reported recipe{(s.Design.Count == 1 ? "" : "s")}; {s.Coupons.SelectMany(c => c.Patches).Count(p => p.Measurement is not null)} with a reported depth.";
        }
        else
        {
            _summary.Text = $"{s.Material.Summary} on {s.Laser.Summary}\n{s.Coupon.Summary} - every coupon must be exactly this size.\n{l.Summary}. Energy ceiling {s.EnergyCeilingJPerMm2:0} J/mm² per patch." + (s.Notes.Length > 0 ? $"\n{s.Notes}" : "");
            int todo = s.UnassignedDesign.Count();
            _designText.Text = $"Design: {s.Design.Count} settings, {s.Design.Count - todo} on coupons, {todo} still to plan - {s.CouponsNeeded()} more coupon{(s.CouponsNeeded() == 1 ? "" : "s")} needed.";
        }
        foreach (var c in s.Coupons)
        {
            string line = c.IsReference
                ? $"{c.Name}  reference  - {c.Patches.Count} reported recipe{(c.Patches.Count == 1 ? "" : "s")}, {c.Patches.Count(p => p.Measurement is not null)} with a reported depth (not measured here)"
                : $"DV{c.Number}  {c.Status}  - {c.Patches.Count} patches" +
                  (c.EngravedUtc is { } e ? $", cut {e.LocalDateTime:yyyy-MM-dd HH:mm}" : "") +
                  (c.Focus is { } f ? $", focus: {f.Summary}" + (c.JobZMm is { } z ? $", job Z {z:0.00}" : "") : "") +
                  (c.Retired ? $" - {c.RetiredReason}" : "");
            _coupons.Items.Add(new ListBoxItem { Tag = c, Content = new TextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = c.Retired || c.IsReference ? Muted : Ink } });
        }
        _plan.IsEnabled = !s.Reference && s.UnassignedDesign.Any() && l.Capacity > 0;
        if (_coupons.Items.Count > 0) _coupons.SelectedIndex = _coupons.Items.Count - 1;
        else { _preview.Show(s, null); ShowCoupon(); }
    }

    private void ShowCoupon()
    {
        var c = Coupon;
        bool live = c is not null && !c.Retired && !c.IsReference && Study is { Reference: false };
        _cut.IsEnabled = live && c!.EngravedUtc is null;
        _saveJob.IsEnabled = live;
        _measure.IsEnabled = live && c!.EngravedUtc is not null;
        _remove.IsEnabled = live;
        _preview.Show(Study, c);
    }

    // ---------------------------------------------------------------- studies

    private async Task NewStudyAsync()
    {
        var dlg = new AblationStudyDialog { Icon = Icon };
        var s = await dlg.ShowDialog<Study?>(this);
        if (s is null) return;
        _db.Studies.Add(s);
        Save();
        FillStudies(s);
        _status.Text = $"Study \"{s.Name}\" created: {s.Design.Count} settings, {s.CouponsNeeded()} coupons needed. Plan the first coupon when the blanks are to hand.";
    }

    private async Task DeleteStudyAsync()
    {
        if (Study is not { } s) return;
        if (!await ConfirmAsync("Delete study", $"Delete \"{s.Name}\" and its {s.Coupons.Count} coupon record(s)? Their test numbers are not reused. The job files stay in the jobs folder.")) return;
        _db.Studies.Remove(s);
        Save();
        FillStudies();
    }

    private async Task ResetAsync()
    {
        if (!await ConfirmAsync("Reset the database", "Start over: every study and coupon record goes, numbering restarts at DV1. The current file is kept beside the new one. Coupons already engraved with a number would get that number again, so this is for a test run, not for a database with real results.")) return;
        var backup = _db.Reset();
        FillStudies();
        _status.Text = backup is null ? "Database reset." : $"Database reset; the old one is kept as {Path.GetFileName(backup)}.";
    }

    private async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a study", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("DepthView study") { Patterns = ["*.dvstudy.json", "*.json"] }],
        });
        if (files.Count == 0) return;
        try
        {
            var s = _db.Import(files[0].Path.LocalPath);
            Save();
            FillStudies(s);
            _status.Text = $"Imported \"{s.Name}\" ({s.Origin}).";
        }
        catch (Exception ex) { await MessageAsync("Import", $"Could not import: {ex.Message}"); }
    }

    private async Task ExportAsync()
    {
        if (Study is not { } s) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the study", SuggestedFileName = Safe(s.Name) + ".dvstudy.json",
            FileTypeChoices = [new FilePickerFileType("DepthView study") { Patterns = ["*.dvstudy.json"] }],
        });
        if (file is null) return;
        try
        {
            AblationDatabase.Export(s, file.Path.LocalPath, Environment.UserName);
            _status.Text = $"Exported to {file.Name}.";
        }
        catch (Exception ex) { await MessageAsync("Export", $"Could not export: {ex.Message}"); }
    }

    private async Task ConvertAsync()
    {
        if (Study is not { } s) return;
        var others = LaserSpec.Presets.Where(l => l.Kind == LaserKind.Mopa && Math.Abs(l.RatedPowerW - s.Laser.RatedPowerW) > 0.5).ToList();
        if (others.Count == 0) { await MessageAsync("Carry to another rating", "No other MOPA rating to carry this to."); return; }
        var to = others[0];
        var copy = PowerConversion.ConvertStudy(s, to);
        int unreachable = copy.DesignReasons.Count(r => r.Contains("not reachable"));
        if (!await ConfirmAsync("Carry to another rating", $"Make \"{copy.Name}\" for the {to.Name}: {copy.Design.Count} settings matched by watts" + (unreachable > 0 ? $", {unreachable} of them capped at 100% because the {to.RatedPowerW:0} W source cannot reach that power" : "") + ". These are predictions until cut and measured; assumes a linear power scale (test T1)."))
            return;
        _db.Studies.Add(copy);
        Save();
        FillStudies(copy);
    }

    // ---------------------------------------------------------------- coupons

    private void PlanCoupon()
    {
        if (Study is not { } s) return;
        var c = _db.PlanCoupon(s);
        if (c is null) { _status.Text = "Nothing left to plan: every setting is on a coupon."; return; }
        Save();
        ShowStudy();
        _status.Text = $"Coupon DV{c.Number} planned with {c.Patches.Count} patches. Put a blank of exactly {s.Coupon.Summary.ToLowerInvariant()} on the bed and cut it.";
    }

    private async Task CutAsync()
    {
        if (Study is not { } s || Coupon is not { } c) return;
        var w = new AblationSendWindow(s, c, _db) { Icon = Icon };
        await w.ShowDialog<bool>(this);
        Save();
        ShowStudy();
    }

    private async Task SaveJobAsync()
    {
        if (Study is not { } s || Coupon is not { } c) return;
        var hot = CouponJob.TooHot(s, c.Patches);
        if (hot.Count > 0) { await MessageAsync("Job", $"{hot.Count} patch(es) over the ceiling of {s.EnergyCeilingJPerMm2:0} J/mm²; the job is not written."); return; }
        string gcode;
        try { gcode = CouponJob.Gcode(s, c, c.BedCentreX ?? 105, c.BedCentreY ?? 105, focusZMm: c.JobZMm); }
        catch (InvalidOperationException ex) { await MessageAsync("Job", $"{ex.Message} Cut it from here (the focus step records the height) rather than saving it."); return; }
        var audit = CouponJob.Audit(s, gcode);
        if (!audit.Ok) { await MessageAsync("Job", audit.Message); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the coupon job", SuggestedFileName = $"DV{c.Number}.gc",
            FileTypeChoices = [new FilePickerFileType("Lumos Ultra job") { Patterns = ["*.gc"] }],
        });
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path.LocalPath, gcode);
        _status.Text = $"Job written to {file.Name}. {audit.Message}";
    }

    private async Task RemoveAsync()
    {
        if (Coupon is not { } c) return;
        var reason = await AskAsync("Remove coupon", $"Retire coupon DV{c.Number}? Its number is never used again; its settings go back to the pool and the re-cut gets a new number. Why (kept with the record)?", "Why it is being removed");
        if (reason is null) return;
        _db.RemoveCoupon(c.Number, reason.Length == 0 ? "removed" : reason);
        Save();
        ShowStudy();
    }

    // ---------------------------------------------------------------- seeds

    /// <summary>
    /// Offers the bundled reference studies, saying plainly where every one of them comes
    /// from, what grade of evidence it is, and that none of it was cut on this machine. Shown
    /// once per seed set on an empty database (and from the button any time). Returns true
    /// when they were added.
    /// </summary>
    public async Task<bool> OfferSeedsAsync(bool unasked)
    {
        var seeds = Seeds.All();
        var w = new Window { Title = "Reference studies bundled with DepthView", Width = 720, SizeToContent = SizeToContent.Height, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon, CanResize = false };
        CurrentDialog = w;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(Text(unasked
            ? "The database is empty. DepthView can add reference studies to start from - but read where they come from first."
            : "DepthView can add (or refresh) its bundled reference studies. Read where they come from first.", 12, Bright, FontWeight.SemiBold));
        panel.Children.Add(Text("None of this was cut or measured on your machine, or on any Lumos Ultra. Each study gathers what one source reported - " +
                                "a vendor's help page, a peer-reviewed paper, a practitioner's blog - on that source's own laser, lens and focus. Every figure is marked " +
                                "PREDICTED with its source and grade (B peer-reviewed with settings; C a vendor's measured figure; E a recipe with no measurement), " +
                                "and a reference study can never be planned, cut or measured. They are a starting point for choosing settings when you cannot run the bench " +
                                "tests yourself, and a comparison for the day you can. Later releases will add studies measured on a known Lumos Ultra, and those will outrank these.", 12, Ink));
        foreach (var seed in seeds)
        {
            var box = new Border { Background = Panel, BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 6) };
            var st = new StackPanel { Spacing = 2 };
            st.Children.Add(Text(seed.Study.Name, 12, Bright, FontWeight.SemiBold));
            st.Children.Add(Text($"From: {seed.From}. Kind: {seed.Kind}. Grade: {seed.Grade}. Contents: {seed.What}.", 11, Ink));
            var urls = seed.Study.Coupons.SelectMany(c => c.Patches).Select(p => p.Provenance?.Url).Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
            if (urls.Count > 0) st.Children.Add(Text("Source: " + string.Join("  ", urls), 10, Muted));
            box.Child = st;
            panel.Children.Add(box);
        }
        panel.Children.Add(Text($"The literature review these are drawn from is {MaterialPrior.ResearchDoc} in the DepthView repository, with every grade explained.", 11, Muted));
        var add = MakeButton("Add the reference studies", "Adds them to the database, marked as reference studies. They can be deleted at any time.");
        var skip = MakeButton(unasked ? "Not now" : "Cancel", "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(skip); buttons.Children.Add(add);
        panel.Children.Add(buttons);
        w.Content = new ScrollViewer { Content = panel, MaxHeight = 760 };
        add.Click += (_, _) => w.Close(true);
        skip.Click += (_, _) => w.Close(false);
        bool yes = await w.ShowDialog<bool>(this);
        if (yes)
        {
            int n = Seeds.AddAll(_db);
            Save();
            FillStudies();
            _status.Text = $"{n} reference stud{(n == 1 ? "y" : "ies")} added - every figure in them is marked with its source; none was cut here.";
        }
        else
        {
            _db.SeedsOfferedVersion = Seeds.Version;   // asked and declined: do not ask again for this set
            Save();
        }
        return yes;
    }

    // ---------------------------------------------------------------- helpers

    private void Save()
    {
        try { _db.Save(); }
        catch (Exception ex) { _status.Text = $"Could not save the database: {ex.Message}"; }
    }

    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)).Trim();

    private async Task<bool> ConfirmAsync(string title, string text) => await AskAsync(title, text, null) is not null;

    /// <summary>A small dialog: the text, an optional answer box, OK and Cancel. Null when cancelled.</summary>
    private async Task<string?> AskAsync(string title, string text, string? watermark)
    {
        var w = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon, CanResize = false };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(Text(text, 12, Ink));
        TextBox? box = null;
        if (watermark is not null) { box = new TextBox { Watermark = watermark }; panel.Children.Add(box); }
        var ok = MakeButton("OK", ""); var cancel = MakeButton("Cancel", "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        w.Content = panel;
        ok.Click += (_, _) => w.Close(box?.Text ?? "");
        cancel.Click += (_, _) => w.Close(null);
        return await w.ShowDialog<string?>(this);
    }

    private async Task MessageAsync(string title, string text)
    {
        var w = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon, CanResize = false };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(Text(text, 12, Ink));
        var ok = MakeButton("OK", ""); ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => w.Close();
        panel.Children.Add(ok);
        w.Content = panel;
        await w.ShowDialog(this);
    }

    private static TextBlock Head(string s) => new() { Text = s, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = B("#9FB4FF"), Margin = new Thickness(0, 6, 0, 2) };
    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new() { Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"), BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }
}
