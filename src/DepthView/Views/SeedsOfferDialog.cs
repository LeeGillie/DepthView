using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DepthView.Ablation;

namespace DepthView.Views;

/// <summary>
/// The offer of the bundled reference studies: where each comes from, what kind of evidence
/// it is and its grade, and that none of it was cut on this machine - shown before anything
/// is added, from the Ablation window (once, unasked, on an empty database, and from its
/// button any time) and from the settings advice when the studies it would rest on are not
/// in the database. On yes the studies are added and the database saved; on no the set is
/// marked as declined so an empty database is not asked again for it.
/// </summary>
public static class SeedsOfferDialog
{
    private static readonly IBrush Bg = B("#101215"), Panel = B("#171A1F"), Ink = B("#DDE3EA"), Bright = B("#EDF1F6"), Muted = B("#8A929E");

    /// <summary>Shows the offer over <paramref name="owner"/>; true when the studies were added. <paramref name="onAdded"/> is told how many.</summary>
    public static async Task<bool> ShowAsync(Window owner, AblationDatabase db, bool unasked, System.Action<int>? onAdded = null, System.Action<Window>? track = null)
    {
        var seeds = Seeds.All();
        var w = new Window { Title = "Reference studies bundled with DepthView", Width = 720, SizeToContent = SizeToContent.Height, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = owner.Icon, CanResize = false };
        track?.Invoke(w);
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(Text(unasked
            ? "The database is empty. DepthView can add reference studies to start from - but read where they come from first."
            : "DepthView can add (or refresh) its bundled reference studies. Read where they come from first.", 12, Bright, FontWeight.SemiBold));
        panel.Children.Add(Text("None of this was cut or measured on your machine, or on any Lumos Ultra. Each study gathers what one source reported - " +
                                "a vendor's help page, a peer-reviewed paper, a practitioner's blog - on that source's own laser, lens and focus. Every figure is marked " +
                                "PREDICTED with its source and grade (B peer-reviewed with settings; C a vendor's measured figure; E a recipe with no measurement), " +
                                "and a reference study can never be planned, cut or measured. They are a starting point for choosing settings when you cannot run the bench " +
                                "tests yourself, and a comparison for the day you can. A measured study from someone with the same laser model and material, imported as a " +
                                "file, outranks all of these; your own coupons outrank everything.", 12, Ink));
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
        bool yes = await w.ShowDialog<bool>(owner);
        if (yes)
        {
            int n = Seeds.AddAll(db);
            db.Save();
            onAdded?.Invoke(n);
        }
        else
        {
            db.SeedsOfferedVersion = Seeds.Version;   // asked and declined: do not ask again for this set
            db.Save();
        }
        return yes;
    }

    private static TextBlock Text(string s, double size, IBrush fg, FontWeight weight = FontWeight.Normal) => new() { Text = s, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 6), Background = B("#22262D"), Foreground = B("#DDE3EA"), BorderBrush = B("#333944"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }
}
