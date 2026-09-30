using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Updates;

namespace DepthView.Views;

/// <summary>
/// What's new in this version: the release notes built into the program, shown in-app.
///
/// Offered by the green bar the first time a newer version starts, and from About at any time.
/// The notes are embedded at build time (see <see cref="ReleaseNotes"/>), so this needs no
/// network; the full release page - downloads, checksums, the commit list - is one click away.
///
/// Built in code, like <see cref="GcodeWindow"/>, for the same reasons.
/// </summary>
public sealed class ReleaseNotesWindow : Window
{
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#DDE3EA"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A929E"));
    private static readonly IBrush Heading = new SolidColorBrush(Color.Parse("#EDF1F6"));
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#BFE3CB"));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    public ReleaseNotesWindow(string? updatedFrom = null)
    {
        Title = $"What's new in DepthView {BuildInfo.Version}";
        Width = 760; Height = 640; MinWidth = 480; MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse("#101215"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        try { Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://DepthView/Assets/depthview-icon-256.png"))); }
        catch { /* the icon is cosmetic */ }

        var title = new TextBlock
        {
            Text = $"DepthView {BuildInfo.Version}",
            FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Heading,
        };
        var sub = new TextBlock
        {
            Text = updatedFrom is null ? "Release notes for the version you are running"
                                       : $"Release notes - updated from {updatedFrom}",
            FontSize = 11, Foreground = Muted,
        };
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#171A1F")),
            BorderBrush = new SolidColorBrush(Color.Parse("#262B33")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10),
            Child = new StackPanel { Spacing = 2, Children = { title, sub } },
        };

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20, 16, 20, 20) };
        var notes = ReleaseNotes.ForThisBuild();
        if (notes is null)
            body.Children.Add(Para(new[] { new ReleaseNotes.Span(
                "This copy does not carry release notes for its own version - a build from source between releases does "
                + "not. The release page has them.") }));
        else
            foreach (var b in ReleaseNotes.Parse(notes))
                body.Children.Add(b.Kind switch
                {
                    ReleaseNotes.BlockKind.Heading => HeadingBlock(b),
                    ReleaseNotes.BlockKind.Bullet => Bullet(b),
                    _ => Para(b.Spans),
                });

        var scroll = new ScrollViewer { Content = body };

        var page = MakeButton("Release page", "Open this version's release page on GitHub: downloads, checksums and every change.");
        page.Click += async (_, _) =>
        {
            try { if (GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri(ReleaseNotes.PageUrl)); }
            catch (Exception) { /* no browser: the address is printed in the footer */ }
        };
        var close = MakeButton("Close", "Close this window.");
        close.Click += (_, _) => Close();

        var link = new TextBlock
        {
            Text = ReleaseNotes.PageUrl, FontSize = 11, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { page, close } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 10) };
        footer.Children.Add(link);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;
    }

    private static Control HeadingBlock(ReleaseNotes.Block b)
    {
        var t = Rich(b.Spans, 16);
        t.FontWeight = FontWeight.SemiBold;
        t.Foreground = Heading;
        t.Margin = new Thickness(0, 0, 0, 4);
        return t;
    }

    private static Control Para(System.Collections.Generic.IReadOnlyList<ReleaseNotes.Span> spans) => Rich(spans, 13);

    private static Control Bullet(ReleaseNotes.Block b)
    {
        var dot = new TextBlock { Text = "•", FontSize = 13, Foreground = Muted, Margin = new Thickness(6, 0, 10, 0) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(dot);
        var t = Rich(b.Spans, 13);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    private static SelectableTextBlock Rich(System.Collections.Generic.IReadOnlyList<ReleaseNotes.Span> spans, double size)
    {
        var tb = new SelectableTextBlock { FontSize = size, Foreground = Text, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.45 };
        tb.Inlines ??= new InlineCollection();
        foreach (var s in spans)
        {
            var run = new Run(s.Text);
            if (s.Bold) run.FontWeight = FontWeight.SemiBold;
            if (s.Italic) run.FontStyle = FontStyle.Italic;
            if (s.Code) { run.FontFamily = Mono; run.Foreground = CodeBrush; run.FontSize = size - 1; }
            tb.Inlines.Add(run);
        }
        return tb;
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
