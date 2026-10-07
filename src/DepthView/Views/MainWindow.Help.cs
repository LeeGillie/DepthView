using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DepthView.Views;

/// <summary>
/// The Help button: the two guides, and this version's notes. The guides open on GitHub at
/// this version's release tag (see <see cref="Guides"/>), so a user who only ever updates in
/// place still reads the edition that matches the program in front of them.
/// </summary>
public partial class MainWindow
{
    private async void OnUserGuide(object? sender, RoutedEventArgs e) => await OpenGuide(Guides.Kind.User);

    private async void OnTuningGuide(object? sender, RoutedEventArgs e) => await OpenGuide(Guides.Kind.Tuning);

    private void OnWhatsNew(object? sender, RoutedEventArgs e) => OpenReleaseNotes();

    private async System.Threading.Tasks.Task OpenGuide(Guides.Kind kind)
    {
        if (await Guides.OpenAsync(TopLevel.GetTopLevel(this), kind) is { } problem)
            StatusText.Text = problem;
    }
}
