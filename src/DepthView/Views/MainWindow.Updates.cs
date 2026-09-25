using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using DepthView.Updates;

namespace DepthView.Views;

/// <summary>
/// The update bar under the header: says when a newer release exists, and installs it.
///
/// Mirrors LUOM What's New. The check runs once when the window opens (at most once a day
/// against GitHub, the rest from a cache), and About - Check for updates runs it on demand.
/// The bar offers What's new, Skip this version and Update now; a copy that cannot replace
/// itself - built from source, renamed, unpacked somewhere read-only - gets the reason and
/// a link to the download page instead of a button that would fail.
/// </summary>
public partial class MainWindow
{
    private static readonly IBrush UpdateTextBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xEB, 0xD7));
    private static readonly IBrush UpdateErrorBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xA0, 0x96));

    private UpdateInfo? _update;
    private bool _updating;

    private void WireUpdates()
    {
        UpdateService.Checked += OnUpdateChecked;
        Closed += (_, _) => UpdateService.Checked -= OnUpdateChecked;

        UpdateNotesButton.Click += async (_, _) => await OpenUrlAsync(_update?.NotesUrl ?? UpdateService.ReleasesPage);
        UpdateDownloadButton.Click += async (_, _) => await OpenUrlAsync(_update?.NotesUrl ?? UpdateService.ReleasesPage);
        UpdateHideButton.Click += (_, _) => UpdateBar.IsVisible = false;
        UpdateSkipButton.Click += (_, _) =>
        {
            Preferences.Current.SkipVersion = _update?.Latest;
            Preferences.Current.Save();
            UpdateBar.IsVisible = false;
        };
        UpdateInstallButton.Click += async (_, _) => await InstallUpdateAsync();

        // Not on a screenshot run: those have to come out the same every time, and a bar that
        // depends on what GitHub says today would make the README images drift. Unless a test
        // feed was given, which is asking for exactly that bar.
        Opened += async (_, _) =>
        {
            bool testFeed = UpdateService.FeedOverride is not null;
            if (!testFeed && (Program.ScreenshotPath is not null || !Preferences.Current.CheckForUpdates)) return;
            var info = await UpdateService.CheckAsync();
            UpdateService.Announce(info, fromUser: false);
        };
    }

    private void OnUpdateChecked(UpdateInfo info, bool fromUser)
    {
        if (_updating) return;
        _update = info;
        RenderUpdateBar(fromUser);
    }

    private void RenderUpdateBar(bool fromUser)
    {
        var u = _update;
        bool show = u is { Newer: true, Error: null }
                    && (fromUser || (Preferences.Current.CheckForUpdates && u.Latest != Preferences.Current.SkipVersion));
        if (!show || u is null) { UpdateBar.IsVisible = false; return; }

        UpdateText.Foreground = UpdateTextBrush;
        UpdateText.Text = u.CanInstall
            ? $"DepthView {u.Latest} is available - you have {u.Current}."
            : $"DepthView {u.Latest} is available - you have {u.Current}. {u.InstallBlocker}";
        UpdateProgress.IsVisible = false;
        UpdateNotesButton.IsVisible = true;
        UpdateSkipButton.IsVisible = true;
        UpdateInstallButton.IsVisible = u.CanInstall;
        UpdateDownloadButton.IsVisible = !u.CanInstall;
        UpdateHideButton.IsVisible = true;
        UpdateBar.IsVisible = true;
    }

    private void ShowUpdateMessage(string text, bool error, bool busy)
    {
        UpdateText.Text = text;
        UpdateText.Foreground = error ? UpdateErrorBrush : UpdateTextBrush;
        UpdateProgress.IsVisible = busy;
        UpdateNotesButton.IsVisible = !busy;
        UpdateSkipButton.IsVisible = false;
        UpdateInstallButton.IsVisible = false;
        UpdateDownloadButton.IsVisible = error;
        UpdateHideButton.IsVisible = !busy;
        UpdateBar.IsVisible = true;
    }

    private async Task InstallUpdateAsync()
    {
        if (_updating || _update is null) return;

        // Restarting closes every window. The tuning dialog can hold work that is not saved yet,
        // so it has to be closed by the person who knows whether it matters.
        if (_tune is not null || _relief is not null)
        {
            ShowUpdateMessage("Close the tuning and relief windows first, so nothing unsaved is lost - then click Update now again.",
                              error: false, busy: false);
            UpdateInstallButton.IsVisible = true;
            return;
        }

        var layout = UpdateService.CurrentLayout();
        if (layout is null) { RenderUpdateBar(true); return; }

        _updating = true;
        string target = _update.Latest ?? "";
        UpdateProgress.IsIndeterminate = true;
        ShowUpdateMessage($"Checking for DepthView {target} ...", error: false, busy: true);

        var progress = new Progress<(string Stage, double? Fraction)>(p =>
        {
            UpdateText.Text = $"{p.Stage} - DepthView {target} ...";
            UpdateProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is double f) UpdateProgress.Value = f;
        });

        try
        {
            // Ask again rather than trust a cached answer: the release could have been replaced.
            var fresh = await UpdateService.CheckAsync(force: true);
            if (fresh.Error is not null) throw new UpdateException(fresh.Error);
            if (!fresh.Newer) throw new UpdateException("This is already the latest version.");
            if (!fresh.CanInstall) throw new UpdateException(fresh.InstallBlocker ?? "This copy cannot be updated in place.");
            target = fresh.Latest ?? target;

            // Off the UI thread: unpacking and hashing a 60 MB bundle would otherwise freeze the window.
            await Task.Run(() => UpdateService.InstallAsync(fresh, layout, progress));
        }
        catch (UpdateException ex)
        {
            _updating = false;
            ShowUpdateMessage($"The update was not installed: {ex.Message} Nothing was changed.", error: true, busy: false);
            return;
        }
        catch (Exception ex)
        {
            _updating = false;
            ShowUpdateMessage($"The update stopped unexpectedly: {ex.Message}", error: true, busy: false);
            return;
        }

        ShowUpdateMessage($"Installed DepthView {target}. Restarting ...", error: false, busy: true);
        try
        {
            Process.Start(UpdateService.RestartStartInfo(layout, Environment.GetCommandLineArgs().Skip(1)));
        }
        catch (Exception ex)
        {
            _updating = false;
            ShowUpdateMessage($"DepthView {target} is installed, but did not start by itself ({ex.Message}). Start DepthView again the usual way.",
                              error: true, busy: false);
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private async Task OpenUrlAsync(string url)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        try { await top.Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception) { StatusText.Text = "Could not open a browser. The address is " + url; }
    }
}
