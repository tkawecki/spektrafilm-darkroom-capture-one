using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

internal static class LookVersionDialogs
{
    internal static string? Name(Window? owner)
    {
        var text = new TextBox { Text = "New look", MaxLength = 120, Margin = new Thickness(0, 8, 0, 12) };
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = "Name this version of the selected variant’s Spektrafilm settings." }); body.Children.Add(text);
        var dialog = Window(owner, "Save look version", body, 470, 180);
        var save = new Button { Content = "Save version", IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(text.Text)) dialog.DialogResult = true; };
        body.Children.Add(save); dialog.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        return dialog.ShowDialog() == true ? text.Text.Trim() : null;
    }
    internal static LookVersion? Choose(Window? owner, IReadOnlyList<LookVersion> versions)
    {
        var body = new DockPanel { Margin = new Thickness(16) };
        var restore = new Button { Content = "Restore selected version", IsDefault = true, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(restore, Dock.Bottom); body.Children.Add(restore);
        var list = new ListBox { ItemsSource = versions, DisplayMemberPath = nameof(LookVersion.Label), SelectedIndex = 0 }; body.Children.Add(list);
        var dialog = Window(owner, "Spektrafilm look versions", body, 560, 400);
        restore.Click += (_, _) => { if (list.SelectedItem is LookVersion) dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? list.SelectedItem as LookVersion : null;
    }
    private static Window Window(Window? owner, string title, UIElement content, double width, double height) =>
        new() { Owner = owner is { IsVisible: true } ? owner : null, Title = title, Content = content, Width = width, Height = height,
            MinWidth = 400, MinHeight = height, WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen };
}
