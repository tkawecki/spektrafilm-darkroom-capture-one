using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal enum RecipeExportChoice { Cancel, Export, Discard }

/// <summary>
/// The list shown before a recipe export: every file, where it goes and what happens there (new, renamed,
/// replaces a file, skipped). The export starts only from here; the list can be turned off for later exports.
/// </summary>
internal static class RecipeExportWindow
{
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(31, 36, 43));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(150, 158, 168));
    private static readonly Brush Warning = Brushes.LightSalmon;

    internal static RecipeExportChoice Confirm(Window? owner, RecipeExportPlan plan, ExportJournal? discarding)
    {
        var writing = plan.Writing.ToArray();
        var window = new Window
        {
            Title = plan.IsResume ? "Resume export – " + StartupHook.ProductName : "Export with recipes – " + StartupHook.ProductName,
            Width = 860, Height = 560, MinWidth = 520, MinHeight = 320,
            Background = Background, Foreground = Brushes.Gainsboro,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = owner is null,
        };
        if (owner is { IsVisible: true }) window.Owner = owner;
        var body = new DockPanel { Margin = new Thickness(16) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = writing.Length == 0 ? "Nothing to export." : $"{Count(writing.Length, "file")} to {Count(plan.FolderCount, "folder")}",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
        });
        var replaced = writing.Count(job => job.File.Outcome == PlannedOutcome.Replaces);
        var renamed = writing.Count(job => job.File.Outcome == PlannedOutcome.Renamed);
        var skipped = plan.Jobs.Count(job => !job.File.Writes);
        var summary = string.Join(" · ", new[]
        {
            renamed > 0 ? $"{renamed} renamed" : null,
            replaced > 0 ? $"{replaced} replace existing files" : null,
            skipped > 0 ? $"{skipped} not exported" : null,
        }.Where(part => part is not null));
        if (summary.Length > 0) header.Children.Add(new TextBlock { Text = summary, Foreground = replaced > 0 ? Warning : Muted, Margin = new Thickness(0, 2, 0, 0) });
        if (discarding is not null)
            header.Children.Add(new TextBlock
            {
                Text = $"An interrupted export of this catalog has {discarding.RemainingCount} files left. Starting this export discards it.",
                Foreground = Warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        foreach (var note in plan.Notes)
            header.Children.Add(new TextBlock { Text = note, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var ask = new CheckBox
        {
            Content = "Show this list before every export", IsChecked = PanelPreferences.Current.ConfirmRecipeExport,
            Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(ask, Dock.Left);
        footer.Children.Add(ask);
        var choice = RecipeExportChoice.Cancel;
        Button Action(string text, RecipeExportChoice result, bool primary = false)
        {
            var button = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
            button.Click += (_, _) => { choice = result; window.DialogResult = result != RecipeExportChoice.Cancel; };
            DockPanel.SetDock(button, Dock.Right);
            return button;
        }
        if (writing.Length > 0) footer.Children.Add(Action((plan.IsResume ? "Resume " : "Export ") + Count(writing.Length, "file"), RecipeExportChoice.Export, primary: true));
        if (plan.IsResume) footer.Children.Add(Action("Discard interrupted export", RecipeExportChoice.Discard));
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        var list = new ListView { Background = Brushes.Transparent, Foreground = Brushes.Gainsboro, BorderThickness = new Thickness(0) };
        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(44, 50, 59))));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Gainsboro));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(58, 66, 77))));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        list.Resources.Add(typeof(GridViewColumnHeader), headerStyle);
        var grid = new GridView();
        GridViewColumn Column(string title, string path, double width) =>
            new() { Header = title, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };
        grid.Columns.Add(Column("Image", nameof(Row.Image), 130));
        grid.Columns.Add(Column("Recipe", nameof(Row.Recipe), 160));
        grid.Columns.Add(Column("File", nameof(Row.File), 170));
        grid.Columns.Add(Column("Folder", nameof(Row.Folder), 230));
        grid.Columns.Add(Column("Action", nameof(Row.Action), 190));
        list.View = grid;
        list.ItemsSource = plan.Jobs.Select(job => new Row(Path.GetFileName(job.File.SourcePath), job.File.RecipeName,
            Path.GetFileName(job.File.OutputPath), Path.GetDirectoryName(job.File.OutputPath) ?? string.Empty, Describe(job.File))).ToArray();
        body.Children.Add(list);

        window.Content = body;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        window.ShowDialog();
        if (ask.IsChecked is bool show && show != PanelPreferences.Current.ConfirmRecipeExport)
            PanelPreferences.Update(preferences => preferences with { ConfirmRecipeExport = show });
        return choice;
    }

    internal static string Describe(PlannedFile file) => file.Outcome switch
    {
        PlannedOutcome.New => "New file",
        PlannedOutcome.Renamed => "Renamed to " + Path.GetFileName(file.OutputPath) + (file.Note is { } note ? " (" + note + ")" : ""),
        PlannedOutcome.Replaces => "Replaces the existing file",
        PlannedOutcome.Skipped => "Skipped: " + file.Note,
        _ => "Not exported: " + file.Note,
    };

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private sealed record Row(string Image, string Recipe, string File, string Folder, string Action);
}
