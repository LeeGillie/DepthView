using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DepthView.Integrations.WeCreat.Gcode;

namespace DepthView.Views;

/// <summary>
/// The --gcode report, in a window: what a G-code job actually sends the machine.
///
/// Opened from the main window when a .gc (or .gcode, .nc, gzipped or not) is browsed to,
/// dropped, or given on the command line. A G-code job is not an image, so it never goes near
/// the image decoder or the histogram; it gets its own window with the same text the command
/// line prints, which keeps one implementation of the report.
///
/// Built in code rather than AXAML because it is a text box and three buttons, and a window
/// with no markup cannot fall into the InitializeComponent trap described in CLAUDE.md.
/// </summary>
public sealed class GcodeWindow : Window
{
    private readonly string _path;
    private readonly TextBox _report;
    private readonly TextBlock _status;
    private readonly Button _copy, _save;

    public GcodeWindow(string path)
    {
        _path = path;
        Title = $"G-code - {Path.GetFileName(path)}";
        Width = 900; Height = 760; MinWidth = 560; MinHeight = 380;
        Background = new SolidColorBrush(Color.Parse("#101215"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://DepthView/Assets/depthview-icon-256.png"))); }
        catch { /* the icon is cosmetic */ }

        var title = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#EDF1F6")),
        };
        var sub = new TextBlock
        {
            Text = "What this G-code job actually sends the machine",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#79818D")),
        };
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#171A1F")),
            BorderBrush = new SolidColorBrush(Color.Parse("#262B33")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10),
            Child = new StackPanel { Spacing = 2, Children = { title, sub } },
        };

        _report = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.Parse("#0C0E11")),
            Foreground = new SolidColorBrush(Color.Parse("#DDE3EA")),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 10),
            Text = "Reading...",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_report, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ToolTip.SetTip(_report, "The same report DepthView --gcode prints. Select any part of it to copy.");

        _status = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8A929E")),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Text = "Reading the file - a large relief job takes a few seconds.",
        };

        _copy = MakeButton("Copy report", "Copy the whole report to the clipboard.");
        _save = MakeButton("Save report...", "Save the report as a text file. The G-code file itself is never changed.");
        var close = MakeButton("Close", "Close this window.");
        _copy.IsEnabled = _save.IsEnabled = false;
        _copy.Click += async (_, _) => await CopyAsync();
        _save.Click += async (_, _) => await SaveAsync();
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _copy, _save, close } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 10) };
        footer.Children.Add(_status);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(_report);
        Content = root;

        Opened += async (_, _) => await AnalyseAsync();
    }

    private async Task AnalyseAsync()
    {
        try
        {
            var a = await Task.Run(() => GcodeAnalyzer.Analyze(_path));
            _report.Text = GcodeReport.Build(a);
            _status.Text = $"{a.Lines:N0} lines read in {a.Elapsed.TotalSeconds:0.0} s.";
            _copy.IsEnabled = _save.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _report.Text = "";
            _status.Text = "Could not read that file: " + ex.Message;
        }
    }

    private async Task CopyAsync()
    {
        var clip = GetTopLevel(this)?.Clipboard;
        if (clip is null || string.IsNullOrEmpty(_report.Text)) return;
        await clip.SetTextAsync(_report.Text);
        _status.Text = "Report copied to the clipboard.";
    }

    private async Task SaveAsync()
    {
        var top = GetTopLevel(this);
        if (top?.StorageProvider is null || string.IsNullOrEmpty(_report.Text)) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save G-code report",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + "-gcode-report.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new[] { new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } } },
        });
        if (file is null) return;

        // The report is a new file; the job it describes is never touched.
        if (file.TryGetLocalPath() is string target && Program.SamePath(target, _path))
        {
            _status.Text = "That is the G-code file itself. Choose a different name.";
            return;
        }

        try
        {
            await using var s = await file.OpenWriteAsync();
            if (s.CanSeek) s.SetLength(0);
            await using var w = new StreamWriter(s);
            await w.WriteAsync(_report.Text);
            _status.Text = $"Saved {file.Name}.";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save: " + ex.Message;
        }
    }

    private static Button MakeButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 7),
            Background = new SolidColorBrush(Color.Parse("#22262D")),
            Foreground = new SolidColorBrush(Color.Parse("#DDE3EA")),
            BorderBrush = new SolidColorBrush(Color.Parse("#333944")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }
}
