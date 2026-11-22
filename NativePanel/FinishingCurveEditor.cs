using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

// Editing is transactional: immutable input is never changed and Cancel returns
// null. The graph evaluates the actual reference-compatible 1024-entry finishing LUT.
internal static class FinishingCurveEditor
{
    internal static ImmutableArray<FinishingPoint>? Edit(Window? owner, string title,
        ImmutableArray<FinishingPoint> points)
    {
        var dialog = new CurveWindow(title, points.IsDefaultOrEmpty ? FinishingCurveStorage.Identity : points);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return dialog.ShowDialog() == true ? dialog.Points : null;
    }

    private sealed class CurveWindow : Window
    {
        private readonly Canvas canvas = new() { Background = new SolidColorBrush(Color.FromRgb(25, 28, 34)),
            ClipToBounds = true, Focusable = true, MinHeight = 220 };
        private readonly TextBox input = new() { Width = 150, Margin = new Thickness(4) };
        private readonly TextBox output = new() { Width = 150, Margin = new Thickness(4) };
        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 36, Margin = new Thickness(0, 8, 0, 0) };
        private int selected;
        private bool dragging;
        internal ImmutableArray<FinishingPoint> Points { get; private set; }

        internal CurveWindow(string title, ImmutableArray<FinishingPoint> points)
        {
            Points = FinishingCurveStorage.Validate(points);
            Title = title;
            Width = 620;
            Height = 640;
            MinWidth = 510;
            MinHeight = 490;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            var root = new DockPanel { Margin = new Thickness(18), Background = SystemColors.WindowBrush };
            Content = root;
            var help = new TextBlock { Text = "Click to add a point. Drag to adjust. Right-click a point to remove it.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(help, Dock.Top);
            root.Children.Add(help);
            var bottom = new StackPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            var numeric = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            numeric.Children.Add(new TextBlock { Text = "Input %", VerticalAlignment = VerticalAlignment.Center });
            numeric.Children.Add(input);
            numeric.Children.Add(new TextBlock { Text = "Output %", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            numeric.Children.Add(output);
            bottom.Children.Add(numeric);
            bottom.Children.Add(status);
            var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var reset = Button("Reset");
            reset.ToolTip = "Restore the straight identity curve.";
            reset.Click += (_, _) => { Points = FinishingCurveStorage.Identity; selected = 0; Redraw(); };
            buttons.Children.Add(reset);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var apply = Button("Apply");
            apply.IsDefault = true;
            apply.Click += (_, _) => { if (CommitNumbers()) DialogResult = true; };
            var cancel = Button("Cancel");
            cancel.IsCancel = true;
            right.Children.Add(apply);
            right.Children.Add(cancel);
            buttons.Children.Add(right);
            bottom.Children.Add(buttons);
            var plot = new DockPanel();
            var label = new TextBlock { Text = "Output ↑", Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(label, Dock.Top);
            plot.Children.Add(label);
            var ticks = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int i = 0; i < 5; ++i)
            {
                ticks.ColumnDefinitions.Add(new ColumnDefinition());
                var tick = new TextBlock { Text = $"{i * 25}%", HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center };
                Grid.SetColumn(tick, i);
                ticks.Children.Add(tick);
            }
            DockPanel.SetDock(ticks, Dock.Bottom);
            plot.Children.Add(ticks);
            plot.Children.Add(canvas);
            root.Children.Add(plot);
            canvas.SizeChanged += (_, _) => Redraw();
            canvas.MouseLeftButtonDown += StartDrag;
            canvas.MouseMove += (_, e) =>
            {
                if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
                Points = FinishingCurveData.Move(Points, selected, ToPoint(e.GetPosition(canvas)));
                Redraw();
            };
            canvas.MouseLeftButtonUp += (_, e) => { dragging = false; canvas.ReleaseMouseCapture(); e.Handled = true; };
            canvas.LostMouseCapture += (_, _) => dragging = false;
            canvas.MouseRightButtonDown += (_, e) =>
            {
                if (!CommitNumbers()) { e.Handled = true; return; }
                int hit = Hit(e.GetPosition(canvas));
                if (hit >= 0) { selected = hit; RemoveSelected(); }
                e.Handled = true;
            };
            canvas.KeyDown += (_, e) => { if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; } };
            input.LostKeyboardFocus += (_, _) => CommitNumbers();
            output.LostKeyboardFocus += (_, _) => CommitNumbers();
            input.KeyDown += NumericKey;
            output.KeyDown += NumericKey;
            Redraw();
        }

        private static Button Button(string text) => new() { Content = text, MinWidth = 80,
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };

        private void NumericKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (CommitNumbers()) canvas.Focus();
            e.Handled = true;
        }

        private bool CommitNumbers()
        {
            var before = Points[selected];
            // Merely focusing/unfocusing a numeric field must not quantize an
            // imported arbitrary coordinate through percent multiplication.
            if (input.Text == Percent(before.X) && output.Text == Percent(before.Y)) return true;
            if (!PercentText.TryParse(input.Text, out double x) || !PercentText.TryParse(output.Text, out double y))
            {
                status.Text = "Enter input and output percentages between 0 and 100.";
                return false;
            }
            x = input.Text == Percent(before.X) ? before.X : x / 100;
            y = output.Text == Percent(before.Y) ? before.Y : y / 100;
            if (selected > 0 && x <= Points[selected - 1].X || selected + 1 < Points.Length && x >= Points[selected + 1].X)
            {
                status.Text = "Input must stay between the adjacent points' input values.";
                return false;
            }
            Points = FinishingCurveData.Move(Points, selected, new(x, y));
            Redraw();
            return true;
        }

        private void StartDrag(object sender, MouseButtonEventArgs e)
        {
            if (!CommitNumbers()) { e.Handled = true; return; }
            var position = e.GetPosition(canvas);
            int hit = Hit(position);
            if (hit < 0)
            {
                try
                {
                    var point = ToPoint(position);
                    Points = FinishingCurveData.Add(Points, point);
                    hit = Points.IndexOf(point);
                }
                catch (ArgumentException error) { status.Text = error.Message; e.Handled = true; return; }
            }
            selected = hit;
            dragging = true;
            // Set fields before Focus raises LostKeyboardFocus, so the previous
            // point's numbers cannot overwrite the newly selected point.
            Redraw();
            canvas.Focus();
            canvas.CaptureMouse();
            e.Handled = true;
        }

        private void RemoveSelected()
        {
            if (Points.Length == 2) { status.Text = "Keep at least two points."; return; }
            Points = FinishingCurveData.Remove(Points, selected);
            selected = Math.Min(selected, Points.Length - 1);
            Redraw();
        }

        private int Hit(Point cursor)
        {
            int hit = -1;
            double best = 12 * 12;
            for (int i = 0; i < Points.Length; ++i)
            {
                var point = ToCanvas(Points[i]);
                double distance = (point - cursor).LengthSquared;
                if (distance <= best) { best = distance; hit = i; }
            }
            return hit;
        }

        private FinishingPoint ToPoint(Point p) => new(Math.Clamp(p.X / Math.Max(canvas.ActualWidth, 1), 0, 1),
            Math.Clamp(1 - p.Y / Math.Max(canvas.ActualHeight, 1), 0, 1));
        private Point ToCanvas(FinishingPoint p) => new(p.X * canvas.ActualWidth, (1 - p.Y) * canvas.ActualHeight);
        private static string Percent(double value) => PercentText.Format(value);

        private void Redraw()
        {
            input.Text = Percent(Points[selected].X);
            output.Text = Percent(Points[selected].Y);
            status.Text = $"Point {selected + 1} of {Points.Length}. Input → output; up to 64 points.";
            canvas.Children.Clear();
            double width = canvas.ActualWidth, height = canvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            var gridBrush = new SolidColorBrush(Color.FromRgb(55, 61, 72));
            for (int i = 0; i <= 4; ++i)
            {
                canvas.Children.Add(new Line { X1 = i * width / 4, X2 = i * width / 4, Y1 = 0, Y2 = height, Stroke = gridBrush });
                canvas.Children.Add(new Line { Y1 = i * height / 4, Y2 = i * height / 4, X1 = 0, X2 = width, Stroke = gridBrush });
            }
            canvas.Children.Add(new Line { X1 = 0, Y1 = height, X2 = width, Y2 = 0,
                Stroke = Brushes.Gray, StrokeDashArray = new DoubleCollection { 3, 4 } });
            var curve = new Polyline { Stroke = Brushes.WhiteSmoke, StrokeThickness = 1.8, IsHitTestVisible = false };
            foreach (var p in FinishingCurveData.Preview(Points)) curve.Points.Add(ToCanvas(p));
            canvas.Children.Add(curve);
            for (int i = 0; i < Points.Length; ++i)
            {
                var point = ToCanvas(Points[i]);
                var marker = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1.5,
                    Stroke = Brushes.White, Fill = i == selected ? Brushes.DarkOrange : Brushes.Teal,
                    IsHitTestVisible = false };
                Canvas.SetLeft(marker, point.X - 5);
                Canvas.SetTop(marker, point.Y - 5);
                canvas.Children.Add(marker);
            }
        }
    }
}

// The operations used by mouse/numeric editing are independently testable; no
// real photographs or OS UI are needed for curve data/preview verification.
internal static class FinishingCurveData
{
    internal static ImmutableArray<FinishingPoint> Add(ImmutableArray<FinishingPoint> points, FinishingPoint point)
    {
        FinishingCurveStorage.Validate(points);
        if (points.Length == FinishingCurveStorage.MaximumPoints) throw new ArgumentException("A curve can contain up to 64 points.");
        return FinishingCurveStorage.Validate(points.Add(point));
    }
    internal static ImmutableArray<FinishingPoint> Move(ImmutableArray<FinishingPoint> points, int index, FinishingPoint point)
    {
        if ((uint)index >= points.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new ArgumentException("Curve coordinates must be finite.");
        double min = index == 0 ? 0 : Math.BitIncrement(points[index - 1].X);
        double max = index == points.Length - 1 ? 1 : Math.BitDecrement(points[index + 1].X);
        var moved = new FinishingPoint(Math.Clamp(point.X, min, max), Math.Clamp(point.Y, 0, 1));
        return moved == points[index] ? points : points.SetItem(index, moved);
    }
    internal static ImmutableArray<FinishingPoint> Remove(ImmutableArray<FinishingPoint> points, int index)
    {
        if ((uint)index >= points.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return points.Length <= 2 ? points : points.RemoveAt(index);
    }
    internal static ImmutableArray<FinishingPoint> Preview(ImmutableArray<FinishingPoint> points, int samples = 257)
    {
        if (samples < 2 || samples > 4096) throw new ArgumentOutOfRangeException(nameof(samples));
        var transform = new FinishingTransform(new FinishingSettings { Curve = points });
        var result = ImmutableArray.CreateBuilder<FinishingPoint>(samples);
        for (int i = 0; i < samples; ++i)
        {
            double x = i / (double)(samples - 1), r = x, g = x, b = x;
            transform.Apply(ref r, ref g, ref b);
            result.Add(new(x, r));
        }
        return result.MoveToImmutable();
    }
}
