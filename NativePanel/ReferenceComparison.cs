using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Read-only comparison of two immutable Viewer snapshots. Pinning retains the
// exact source frame, independently of later edits or a different photo.
internal sealed class ReferenceComparison : Window
{
    private readonly Surface reference, current;
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Side by side", "Vertical wipe", "Reference only", "Current only" }, SelectedIndex = 0, MinWidth = 145, Margin = new Thickness(5) };
    private readonly Slider divider = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 220, Margin = new Thickness(10, 5, 10, 5) };
    private readonly Grid images = new() { ClipToBounds = true, Background = Brushes.Black };
    internal BitmapSource ReferenceFrame { get; }
    internal BitmapSource CurrentFrame { get; private set; }
    internal bool IsClosed { get; private set; }
    private readonly string referenceLabel;

    internal ReferenceComparison(Window? owner, BitmapSource pinned, string label, BitmapSource live)
    {
        if (!pinned.IsFrozen || !live.IsFrozen) throw new ArgumentException("Comparison requires immutable Viewer frames.");
        ReferenceFrame = pinned; CurrentFrame = live; referenceLabel = label;
        Title = "Spektrafilm reference comparison"; Width = 1200; Height = 760; MinWidth = 650; MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 32)); Foreground = Brushes.Gainsboro;
        if (owner is { IsVisible: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        var root = new DockPanel { Margin = new Thickness(10) }; Content = root;
        var controls = new WrapPanel(); DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        controls.Children.Add(mode); controls.Children.Add(divider);
        controls.Children.Add(new TextBlock { Text = "Read-only · follows the current Viewer · reference stays pinned", Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(description, Dock.Bottom); root.Children.Add(description); root.Children.Add(images);
        reference = new Surface(pinned); current = new Surface(live);
        images.Children.Add(reference); images.Children.Add(current);
        mode.SelectionChanged += (_, _) => LayoutImages(); divider.ValueChanged += (_, _) => LayoutImages();
        images.SizeChanged += (_, _) => LayoutImages();
        Closed += (_, _) => { IsClosed = true; reference.Dispose(); current.Dispose(); };
        Update(live); LayoutImages();
    }
    internal void Update(BitmapSource frame)
    {
        if (IsClosed) return;
        if (!frame.IsFrozen) throw new ArgumentException("Comparison requires an immutable frame.");
        CurrentFrame = frame; current.Update(frame);
        description.Text = $"Reference: {referenceLabel} · {ReferenceFrame.PixelWidth} × {ReferenceFrame.PixelHeight}\nCurrent Viewer: {frame.PixelWidth} × {frame.PixelHeight}. Displayed in sRGB through the monitor profile; this is not a wide-gamut export proof.";
    }
    private void LayoutImages()
    {
        double width = Math.Max(0, images.ActualWidth), height = Math.Max(0, images.ActualHeight);
        reference.Visibility = mode.SelectedIndex == 3 ? Visibility.Hidden : Visibility.Visible;
        current.Visibility = mode.SelectedIndex == 2 ? Visibility.Hidden : Visibility.Visible;
        divider.IsEnabled = mode.SelectedIndex == 1;
        reference.HorizontalAlignment = current.HorizontalAlignment = HorizontalAlignment.Stretch;
        reference.Margin = current.Margin = new Thickness(0); reference.Clip = current.Clip = null;
        if (mode.SelectedIndex == 0) {
            reference.Margin = new Thickness(0, 0, width / 2 + 3, 0);
            current.Margin = new Thickness(width / 2 + 3, 0, 0, 0);
        } else if (mode.SelectedIndex == 1) {
            current.Clip = new RectangleGeometry(new Rect(width * divider.Value, 0, width * (1 - divider.Value), height));
        }
    }
    private sealed class Surface : FrameworkElement, IDisposable
    {
        private BitmapSource image;
        private DrawingGroup original;
        private readonly MonitorColorManagement colors;
        private bool disposed;
        internal Surface(BitmapSource frame)
        {
            image = frame; original = PreviewDrawing.Create(frame);
            colors = new MonitorColorManagement(Dispatcher, InvalidateVisual, message => Debug.WriteLine(message));
            SizeChanged += (_, _) => InvalidateVisual();
        }
        internal void Update(BitmapSource frame)
        {
            if (ReferenceEquals(image, frame)) return;
            image = frame; original = PreviewDrawing.Create(frame); InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing); if (disposed || ActualWidth <= 0 || ActualHeight <= 0) return;
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double scale = Math.Min(ActualWidth / image.PixelWidth, ActualHeight / image.PixelHeight);
            var painted = colors.GetDrawing(this, image, original); ToolTip = colors.Warning;
            drawing.PushTransform(new TranslateTransform((ActualWidth - image.PixelWidth * scale) / 2, (ActualHeight - image.PixelHeight * scale) / 2));
            drawing.PushTransform(new ScaleTransform(scale, scale)); drawing.DrawDrawing(painted); drawing.Pop(); drawing.Pop();
        }
        public void Dispose() { if (disposed) return; disposed = true; colors.Dispose(); }
    }
}
