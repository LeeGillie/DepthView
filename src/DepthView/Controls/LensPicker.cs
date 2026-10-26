using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DepthView.Ablation;

namespace DepthView.Controls;

/// <summary>
/// The lens, wherever one is asked for: a list that starts with the lenses the Lumos Ultra
/// comes with, then the common third-party f-theta sizes, then "Other lens (type it)", which
/// opens a box. <see cref="Text"/> is the lens's name, the same string the profile, the focus
/// record and the study store, so nothing written by an earlier version is lost: a stored
/// name that is not in the list shows as Other with the name in the box.
/// </summary>
public sealed class LensPicker : UserControl
{
    private readonly ComboBox _list = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
    private readonly TextBox _other = new() { Watermark = "Name the lens, e.g. F160 110 x 110 mm, maker", FontSize = 12, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
    private readonly TextBlock _note = new() { FontSize = 10.5, Foreground = Avalonia.Media.Brushes.Gray, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private bool _syncing;

    /// <summary>Raised when the lens's name changed, by the list or by typing.</summary>
    public event EventHandler? Changed;

    public LensPicker()
    {
        string? group = null;
        foreach (var l in LensCatalog.All)
        {
            // A heading row per group, not selectable, so the choices read as what they are.
            if (l.Group != group)
            {
                group = l.Group;
                _list.Items.Add(new ComboBoxItem { Content = group, IsEnabled = false, FontSize = 10.5, Foreground = Avalonia.Media.Brushes.Gray });
            }
            _list.Items.Add(new ComboBoxItem { Content = l.Label, Tag = l });
        }
        _list.SelectedIndex = 1;
        var stack = new StackPanel();
        stack.Children.Add(_list);
        stack.Children.Add(_other);
        stack.Children.Add(_note);
        Content = stack;
        ToolTip.SetTip(_list, "Which lens is fitted. The Lumos Ultra's own lenses come first and are the default; a third-party f-theta lens changes the field, the spot and the focus, and a study cut with one is a study of that lens.");

        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var c = Selected;
            _other.IsVisible = c?.Group == LensChoice.Other;
            _note.Text = c?.Note ?? "";
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _other.TextChanged += (_, _) => { if (!_syncing) Changed?.Invoke(this, EventArgs.Empty); };
        _note.Text = Selected?.Note ?? "";
    }

    private LensChoice? Selected => (_list.SelectedItem as ComboBoxItem)?.Tag as LensChoice;

    /// <summary>The lens's name: a listed lens's name, or what was typed under Other. Empty when nothing is named.</summary>
    public string Text
    {
        get
        {
            var c = Selected;
            if (c is null) return "";
            return c.Group == LensChoice.Other ? (_other.Text ?? "").Trim() : c.Name;
        }
        set
        {
            _syncing = true;
            try
            {
                var found = LensCatalog.Find(value);
                if (found is null && string.IsNullOrWhiteSpace(value)) found = LensCatalog.Default;
                int idx = -1;
                for (int i = 0; i < _list.Items.Count; i++)
                    if ((_list.Items[i] as ComboBoxItem)?.Tag is LensChoice lc && (found is not null ? ReferenceEquals(lc, found) : lc.Group == LensChoice.Other)) { idx = i; break; }
                _list.SelectedIndex = Math.Max(0, idx);
                bool other = found is null;
                _other.IsVisible = other;
                if (other) _other.Text = value?.Trim() ?? "";
                _note.Text = Selected?.Note ?? "";
            }
            finally { _syncing = false; }
        }
    }

    /// <summary>The listed lens, when the choice is one; null for a hand-typed lens.</summary>
    public LensChoice? Choice => Selected is { } c && c.Group != LensChoice.Other ? c : null;
}
