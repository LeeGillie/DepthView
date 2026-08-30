using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DepthView.Views;

/// <summary>
/// Version, supported platforms and a self scrolling credit roll.
///
/// The roll drives ScrollViewer.Offset from a timer rather than animating a transform,
/// so the wheel and the scrollbar keep working while it moves: whatever the user does to
/// the offset, the next tick simply carries on from wherever it now is.
/// </summary>
public partial class AboutWindow : Window
{
    // 30 fps at 0.85 px a tick is about 26 px a second: slow enough to read a two line
    // entry without chasing it, fast enough that the whole roll takes well under a minute.
    private const double PixelsPerTick = 0.85;
    private const int TicksAtEnd = 75;      // 2.5 s pause on the last line
    private const int TicksAtStart = 45;    // 1.5 s pause before it sets off again

    private readonly DispatcherTimer _roll;
    private int _hold = TicksAtStart;
    private bool _atEnd;
    private bool _running = true;
    private bool _hovering;
    private bool _showingLicence;

    public AboutWindow()
    {
        InitializeComponent();

        VersionLine.Text = $"Version {BuildInfo.Version}   -   built {BuildInfo.BuildDate}";

        BuildPlatforms();
        BuildDetails();
        FillCredits();

        _roll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _roll.Tick += (_, _) => Advance();
        _roll.Start();

        CreditScroll.PointerEntered += (_, _) => _hovering = true;
        CreditScroll.PointerExited += (_, _) => _hovering = false;

        ScrollToggle.Click += (_, _) =>
        {
            _running = !_running;
            ScrollToggle.Content = _running ? "Pause" : "Play";
            CreditHint.Text = _running ? "hover to pause" : "paused";
        };

        CopyBuildButton.Click += async (_, _) =>
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(BuildInfo.Signature);
            Flash("Build info copied.");
        };

        LicenceButton.Click += (_, _) => ToggleLicence();
        CloseButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        if (Program.StartupLicence) ToggleLicence();
    }

    /// <summary>Move the roll on by one frame, honouring the pauses and the loop.</summary>
    private void Advance()
    {
        if (_showingLicence || !_running || _hovering) return;

        double max = Math.Max(0, CreditScroll.Extent.Height - CreditScroll.Viewport.Height);
        if (max <= 0.5) return;   // window is tall enough to show everything; nothing to roll

        if (_hold > 0)
        {
            _hold--;
            if (_hold == 0 && _atEnd)
            {
                CreditScroll.Offset = new Vector(0, 0);
                _atEnd = false;
                _hold = TicksAtStart;
            }
            return;
        }

        double y = CreditScroll.Offset.Y + PixelsPerTick;
        if (y >= max)
        {
            CreditScroll.Offset = new Vector(0, max);
            _atEnd = true;
            _hold = TicksAtEnd;
            return;
        }

        CreditScroll.Offset = new Vector(0, y);
    }

    private void BuildPlatforms()
    {
        foreach (var (name, detail) in BuildInfo.Platforms)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
            block.Children.Add(new TextBlock
            {
                Text = name,
                Classes = { "value" },
                FontSize = 12.5,
            });
            block.Children.Add(new TextBlock
            {
                Text = detail,
                Classes = { "creditnote" },
            });
            PlatformList.Children.Add(block);
        }

        PlatformList.Children.Add(new TextBlock
        {
            Text = "Each is one self contained file. No runtime to install, nothing to unpack.",
            Classes = { "creditnote" },
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#6FA97F")),
        });
    }

    private void BuildDetails()
    {
        AddDetail("Version", BuildInfo.Version, "The release number. Compare it against the latest tag on the repository.");
        AddDetail("Built", BuildInfo.BuildDate, "UTC date this binary was compiled.");
        AddDetail("Packaging", BuildInfo.Packaging, "Whether this is a published release binary or a build from source.");
        AddDetail("Runtime", BuildInfo.Runtime, "The .NET runtime actually executing, which for a release build travels inside the file.");
        AddDetail("Host", BuildInfo.Host, "The operating system and process architecture reported by the runtime.");
        AddDetail("Interface", BuildInfo.UiToolkit, "The UI toolkit version linked into this build.");
        AddDetail("Imaging", BuildInfo.ImageLibrary, "Used for the convenience formats only. The depth critical decoders are DepthView's own.");
    }

    private void AddDetail(string label, string value, string tip)
    {
        int row = BuildGrid.RowDefinitions.Count;
        BuildGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var labelBlock = new TextBlock
        {
            Text = label,
            Classes = { "label" },
            Margin = new Thickness(0, 0, 8, 6),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(labelBlock, tip);
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            Classes = { "value" },
            Margin = new Thickness(0, 0, 0, 6),
        };
        ToolTip.SetTip(valueBlock, tip);
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 1);

        BuildGrid.Children.Add(labelBlock);
        BuildGrid.Children.Add(valueBlock);
    }

    private void FillCredits()
    {
        CreditBody.Children.Clear();

        foreach (var section in Credits.Sections)
        {
            CreditBody.Children.Add(new TextBlock
            {
                Text = section.Role,
                Classes = { "creditrole" },
            });

            foreach (var entry in section.Entries)
            {
                CreditBody.Children.Add(new TextBlock
                {
                    Text = entry.Name,
                    Classes = { "creditname" },
                });
                CreditBody.Children.Add(new TextBlock
                {
                    Text = entry.Note,
                    Classes = { "creditnote" },
                    MaxWidth = 620,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
            }
        }

        CreditBody.Children.Add(new TextBlock
        {
            Text = "Thank you for reading to the end.",
            Classes = { "creditnote" },
            Margin = new Thickness(0, 26, 0, 0),
            FontStyle = FontStyle.Italic,
        });
    }

    private void ToggleLicence()
    {
        _showingLicence = !_showingLicence;

        if (_showingLicence)
        {
            PanelTitle.Text = "LICENCE";
            LicenceButton.Content = "Credits";
            CreditHint.Text = "scroll to read";
            ScrollToggle.IsEnabled = false;

            CreditBody.Children.Clear();
            CreditBody.Children.Add(new TextBlock
            {
                Text = LicenceText,
                Classes = { "creditnote" },
                FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
                FontSize = 11,
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }
        else
        {
            PanelTitle.Text = "CREDITS";
            LicenceButton.Content = "Licence";
            CreditHint.Text = _running ? "hover to pause" : "paused";
            ScrollToggle.IsEnabled = true;
            FillCredits();
        }

        CreditScroll.Offset = new Vector(0, 0);
        _atEnd = false;
        _hold = TicksAtStart;
    }

    private void Flash(string message)
    {
        CopyFeedback.Text = message;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        clear.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            CopyFeedback.Text = string.Empty;
        };
        clear.Start();
    }

    private static readonly string LicenceText = BuildLicenceText();

    private static string BuildLicenceText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MIT License");
        sb.AppendLine();
        sb.AppendLine("Copyright (c) 2026 Lee Gillie");
        sb.AppendLine();
        sb.AppendLine("Permission is hereby granted, free of charge, to any person obtaining a copy");
        sb.AppendLine("of this software and associated documentation files (the \"Software\"), to deal");
        sb.AppendLine("in the Software without restriction, including without limitation the rights");
        sb.AppendLine("to use, copy, modify, merge, publish, distribute, sublicense, and/or sell");
        sb.AppendLine("copies of the Software, and to permit persons to whom the Software is");
        sb.AppendLine("furnished to do so, subject to the following conditions:");
        sb.AppendLine();
        sb.AppendLine("The above copyright notice and this permission notice shall be included in all");
        sb.AppendLine("copies or substantial portions of the Software.");
        sb.AppendLine();
        sb.AppendLine("THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR");
        sb.AppendLine("IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,");
        sb.AppendLine("FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE");
        sb.AppendLine("AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER");
        sb.AppendLine("LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,");
        sb.AppendLine("OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE");
        sb.AppendLine("SOFTWARE.");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("THIRD PARTY COMPONENTS SHIPPED INSIDE THIS BINARY");
        sb.AppendLine();
        sb.AppendLine("  Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent,");
        sb.AppendLine("  Avalonia.Fonts.Inter .......................... MIT");
        sb.AppendLine("  .NET runtime and libraries, Microsoft ......... MIT");
        sb.AppendLine("  HarfBuzz and SkiaSharp bindings ............... MIT");
        sb.AppendLine("  ANGLE ......................................... BSD 3 Clause");
        sb.AppendLine("  Inter typeface, Rasmus Andersson .............. SIL Open Font Licence 1.1");
        sb.AppendLine("  SixLabors.ImageSharp .......................... Six Labors Split Licence");
        sb.AppendLine();
        sb.AppendLine("The Six Labors Split Licence grants Apache 2.0 terms to software released");
        sb.AppendLine("under an open source or source available licence. DepthView is MIT, so those");
        sb.AppendLine("terms apply here and carry no further obligation.");
        sb.AppendLine();
        sb.AppendLine("Full texts and package versions are in THIRD-PARTY-NOTICES.md, distributed");
        sb.AppendLine("with the source at github.com/LeeGillie/DepthView");
        return sb.ToString();
    }
}
