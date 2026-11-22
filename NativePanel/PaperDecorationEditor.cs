using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

// Transactional editor. The caller applies the returned immutable snapshot only
// after checking that its variant/document still matches the opening context.
internal static class PaperDecorationEditor
{
    internal static PaperDecorationSettings? Edit(Window? owner, PaperDecorationSettings? current, PaperTextureStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var dialog = new EditorWindow(current ?? PaperDecorationSettings.Default, store);
        if (owner is { IsVisible: true }) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    internal sealed class EditorWindow : Window
    {
        private readonly PaperDecorationSettings original;
        private readonly PaperTextureStore store;
        private readonly CancellationTokenSource importCancellation = new();
        private readonly CheckBox active = new() { Content = "Enable paper finish", Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox burn = new() { Content = "Burn border into image", VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox padding = new() { Width = 105, Margin = new Thickness(10, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ListBox list = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 130 };
        private readonly ObservableCollection<LayerRow> rows = new();
        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 34, Margin = new Thickness(0, 10, 0, 0) };
        private readonly Button add = Button("Add texture…"), remove = Button("Remove"), up = Button("Move up"), down = Button("Move down"), apply = Button("Apply"), reset = Button("Reset");
        private bool importing, closed;
        internal PaperDecorationSettings? Result { get; private set; }

        internal EditorWindow(PaperDecorationSettings? current, PaperTextureStore store)
        {
            original = current ?? PaperDecorationSettings.Default; original.Validate(); this.store = store ?? throw new ArgumentNullException(nameof(store));
            Title = "Paper texture and border"; Width = 780; Height = 570; MinWidth = 650; MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
            Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
            var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(new TextBlock { Text = "Layers are applied from top to bottom. Apply updates this look; Cancel leaves it unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var toggles = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; toggles.Children.Add(active); toggles.Children.Add(burn); header.Children.Add(toggles);
            var border = new StackPanel { Orientation = Orientation.Horizontal }; border.Children.Add(new TextBlock { Text = "Border width (% of long edge)", VerticalAlignment = VerticalAlignment.Center }); border.Children.Add(padding); header.Children.Add(border);
            padding.ToolTip = "0 to 100%. The white border is part of the image only when burn is enabled and the stack contains a layer.";
            AutomationProperties.SetName(padding, "Border width, percent of long edge");
            var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            bottom.Children.Add(status);
            var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) }; bottom.Children.Add(actions);
            reset.Click += (_, _) => { active.IsChecked = false; burn.IsChecked = false; padding.Text = Percent(.03); rows.Clear(); RefreshButtons(); status.Text = ""; }; actions.Children.Add(reset);
            var confirm = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(confirm);
            apply.IsDefault = true; apply.Click += (_, _) => { if (TryAccept()) DialogResult = true; }; confirm.Children.Add(apply);
            var cancel = Button("Cancel"); cancel.IsCancel = true; confirm.Children.Add(cancel);
            var center = new DockPanel(); root.Children.Add(center);
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(toolbar, Dock.Top); center.Children.Add(toolbar);
            foreach (var button in new[] { add, remove, up, down }) toolbar.Children.Add(button);
            list.ItemsSource = rows; center.Children.Add(list); AutomationProperties.SetName(list, "Paper texture layers in application order");
            add.Click += async (_, _) => await AddTexture(); remove.Click += (_, _) => RemoveSelected(); up.Click += (_, _) => MoveSelected(-1); down.Click += (_, _) => MoveSelected(1);
            list.SelectionChanged += (_, _) => RefreshButtons();
            active.IsChecked = original.Active; burn.IsChecked = original.PaddingBurn; padding.Text = Percent(original.PaddingFraction);
            if (!original.Layers.IsDefaultOrEmpty) foreach (var layer in original.Layers) AddRow(layer);
            if (rows.Count > 0) list.SelectedIndex = 0;
            Closing += (_, _) => { closed = true; importCancellation.Cancel(); if (DialogResult != true) Result = null; };
            Closed += (_, _) => importCancellation.Dispose();
            RefreshButtons();
        }
        private static Button Button(string text) => new() { Content = text, MinWidth = 82, MinHeight = 32,
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        private static string Percent(double value) => PercentText.Format(value);
        private static bool TryPercent(string text, double original, out double value)
        {
            // An untouched editor must not round an imported double through
            // multiplication/division by 100.
            if (text == Percent(original)) { value = original; return true; }
            if (PercentText.TryParse(text, out value)) { value /= 100; return true; }
            return false;
        }
        internal bool TryAccept()
        {
            if (importing) return false;
            if (!TryPercent(padding.Text, original.PaddingFraction, out double fraction))
            { status.Text = "Enter a border width between 0 and 100%."; padding.Focus(); return false; }
            var layers = ImmutableArray.CreateBuilder<PaperTextureLayer>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].TryLayer(out var layer))
                { status.Text = $"Enter an opacity between 0 and 100% for layer {i + 1}."; list.SelectedIndex = i; rows[i].OpacityField.Focus(); return false; }
                layers.Add(layer!);
            }
            var candidate = new PaperDecorationSettings(active.IsChecked == true, burn.IsChecked == true, fraction, layers.MoveToImmutable());
            try { candidate.Validate(); }
            catch (ArgumentException error) { status.Text = error.Message; return false; }
            bool same = candidate.Active == original.Active && candidate.PaddingBurn == original.PaddingBurn && candidate.PaddingFraction == original.PaddingFraction
                && candidate.Layers.SequenceEqual(original.Layers.IsDefault ? ImmutableArray<PaperTextureLayer>.Empty : original.Layers);
            Result = same ? original : candidate; status.Text = ""; return true;
        }
        private async Task AddTexture()
        {
            if (importing || rows.Count >= 32) return;
            var picker = new OpenFileDialog { Title = "Import paper texture", Filter = "Texture images|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.webp", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            importing = true; RefreshButtons(); status.Text = "Importing texture…";
            var token = importCancellation.Token; string path = picker.FileName;
            try
            {
                var asset = await Task.Run(() => store.Import(path, token), token);
                if (closed) return;
                AddRow(new(asset.AssetId, Name: asset.Name)); active.IsChecked = true; list.SelectedIndex = rows.Count - 1;
                status.Text = $"Imported {asset.Name} · {asset.Width} × {asset.Height} px.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed) status.Text = "Could not import texture: " + error.GetBaseException().Message; }
            finally { importing = false; if (!closed) RefreshButtons(); }
        }
        private void AddRow(PaperTextureLayer layer)
        {
            var row = new LayerRow(layer); row.PreviewMouseDown += (_, _) => list.SelectedItem = row;
            row.GotKeyboardFocus += (_, _) => list.SelectedItem = row; rows.Add(row);
        }
        private void RemoveSelected()
        {
            int index = list.SelectedIndex; if (importing || index < 0) return;
            rows.RemoveAt(index); list.SelectedIndex = Math.Min(index, rows.Count - 1); RefreshButtons();
        }
        private void MoveSelected(int delta)
        {
            int index = list.SelectedIndex, target = index + delta;
            if (importing || index < 0 || target < 0 || target >= rows.Count) return;
            rows.Move(index, target); list.SelectedIndex = target; RefreshButtons();
        }
        private void RefreshButtons()
        {
            int selected = list.SelectedIndex; add.IsEnabled = !importing && rows.Count < 32; apply.IsEnabled = !importing;
            reset.IsEnabled = active.IsEnabled = burn.IsEnabled = padding.IsEnabled = !importing;
            remove.IsEnabled = !importing && selected >= 0; up.IsEnabled = !importing && selected > 0;
            down.IsEnabled = !importing && selected >= 0 && selected + 1 < rows.Count; list.IsEnabled = !importing;
        }
        private sealed class ModeChoice(PaperBlendMode mode)
        { internal PaperBlendMode Mode { get; } = mode; public override string ToString() => PaperDecoration.ModeName(Mode); }
        private sealed class LayerRow : Grid
        {
            private readonly PaperTextureLayer original;
            private readonly ComboBox mode = new() { MinWidth = 125, Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            internal TextBox OpacityField { get; } = new() { Width = 115, VerticalContentAlignment = VerticalAlignment.Center };
            internal LayerRow(PaperTextureLayer layer)
            {
                original = layer; Margin = new Thickness(3, 5, 3, 5); MinHeight = 32;
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string name = string.IsNullOrEmpty(layer.Name) ? layer.AssetId[..12] : layer.Name;
                var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = name + "\n" + layer.AssetId };
                Children.Add(label); mode.ItemsSource = Enum.GetValues<PaperBlendMode>().Select(x => new ModeChoice(x)).ToArray();
                mode.SelectedItem = mode.Items.Cast<ModeChoice>().First(x => x.Mode == layer.Mode); Grid.SetColumn(mode, 1); Children.Add(mode);
                var amount = new StackPanel { Orientation = Orientation.Horizontal }; OpacityField.Text = Percent(layer.Opacity); amount.Children.Add(OpacityField);
                amount.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) }); Grid.SetColumn(amount, 2); Children.Add(amount);
                AutomationProperties.SetName(mode, name + " blend mode"); AutomationProperties.SetName(OpacityField, name + " opacity percent");
                OpacityField.ToolTip = "Layer opacity, 0 to 100%.";
            }
            internal bool TryLayer(out PaperTextureLayer? result)
            {
                result = null;
                if (mode.SelectedItem is not ModeChoice selected || !TryPercent(OpacityField.Text, original.Opacity, out double opacity)) return false;
                result = original with { Mode = selected.Mode, Opacity = opacity }; return true;
            }
        }
    }
}
