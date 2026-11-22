using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Diagnostic only: callers supply the original sRGB Viewer frame, before its
// monitor-profile Paint conversion. No host capture, polling or export access.
internal static class ImageScopes
{
    internal static ScopeWindow Show(Window? owner, BitmapSource frame, string title)
    {
        var window = new ScopeWindow(title);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        window.Show();
        window.Update(frame);
        return window;
    }

    internal sealed record Computed(ImageScopesMath.Result Data, BitmapSource RgbWaveform,
        BitmapSource LumaWaveform, BitmapSource Vector);

    internal static BitmapSource FreezeSnapshot(BitmapSource frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsFrozen) return frame;
        var snapshot = frame.CloneCurrentValue();
        snapshot.Freeze();
        return snapshot;
    }

    // Runs entirely on a worker. Frozen WPF bitmaps permit cross-thread reads.
    internal static Computed Compute(BitmapSource snapshot, CancellationToken token,
        int maximumSamples = ImageScopesMath.DefaultMaximumSamples)
    {
        token.ThrowIfCancellationRequested();
        if (!snapshot.IsFrozen) throw new ArgumentException("Freeze the frame snapshot before background analysis.", nameof(snapshot));
        BitmapSource source = snapshot;
        if (source.Format != PixelFormats.Bgra32)
        {
            // Format conversion only, no ICC transform. Converts premultiplied
            // alpha to straight RGB and explicitly quantizes high-depth inputs
            // to the same 8-bit scope code range shown in the window.
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            source.Freeze();
        }
        var plan = ImageScopesMath.Plan(source.PixelWidth, source.PixelHeight, maximumSamples);
        var pixels = new byte[checked(plan.Count * 4)];
        var row = new byte[checked(source.PixelWidth * 4)];
        for (int y = 0; y < plan.Height; ++y)
        {
            token.ThrowIfCancellationRequested();
            source.CopyPixels(new Int32Rect(0, plan.SourceY(y), source.PixelWidth, 1), row, row.Length, 0);
            if (plan.Width == source.PixelWidth) Buffer.BlockCopy(row, 0, pixels, y * plan.Width * 4, row.Length);
            else for (int x = 0; x < plan.Width; ++x)
            {
                int start = plan.SourceX(x) * 4, output = (y * plan.Width + x) * 4;
                pixels[output] = row[start]; pixels[output + 1] = row[start + 1];
                pixels[output + 2] = row[start + 2]; pixels[output + 3] = row[start + 3];
            }
        }
        var data = ImageScopesMath.Analyze(pixels, plan, token);
        return new(data, Density(data.Waveform.Take(3).ToArray(), false, token),
            Density(new[] { data.Waveform[3] }, false, token), Density(new[] { data.Vectorscope }, true, token));
    }

    private static BitmapSource Density(int[][] planes, bool green, CancellationToken token)
    {
        int peak = planes.Max(x => x.Max());
        var bytes = new byte[256 * 256 * 4];
        double denominator = Math.Log(1d + peak);
        byte Level(int value) => value == 0 ? (byte)0 : (byte)Math.Round(255 * (.2 + .8 * Math.Log(1d + value) / denominator));
        for (int y = 0; y < 256; ++y)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < 256; ++x)
            {
                int source = y * 256 + x, output = ((255 - y) * 256 + x) * 4;
                if (planes.Length == 3)
                {
                    bytes[output] = Level(planes[2][source]); bytes[output + 1] = Level(planes[1][source]); bytes[output + 2] = Level(planes[0][source]);
                }
                else
                {
                    byte level = Level(planes[0][source]);
                    bytes[output + 1] = level;
                    bytes[output] = bytes[output + 2] = green ? (byte)(level * .62) : level;
                }
                bytes[output + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, bytes, 1024);
        bitmap.Freeze(); return bitmap;
    }

    internal sealed class ScopeWindow : Window
    {
        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBlock clipping = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly ScopePlot histogram = new(PlotKind.Histogram), waveform = new(PlotKind.Waveform), vectorscope = new(PlotKind.Vector);
        private readonly CheckBox luma = new() { Content = "Luma waveform", Margin = new Thickness(0, 6, 20, 6) };
        private readonly CheckBox logHistogram = new() { Content = "Log histogram", IsChecked = true, Margin = new Thickness(0, 6, 0, 6) };
        private Computed? current;
        private CancellationTokenSource? pending;
        private BitmapSource? lastFrame;
        internal bool IsClosed { get; private set; }
        internal Task PendingUpdate { get; private set; } = Task.CompletedTask;
        internal ImageScopesMath.Result? CurrentResult => current?.Data;

        internal ScopeWindow(string title)
        {
            Title = title; Width = 1050; Height = 760; MinWidth = 760; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
            Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush;
            var body = new DockPanel { Margin = new Thickness(16), Background = SystemColors.WindowBrush }; Content = body;
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            top.Children.Add(new TextBlock { Text = "Spektrafilm output · sRGB code values", FontSize = 16, FontWeight = FontWeights.SemiBold });
            top.Children.Add(new TextBlock { Text = "RGB and Rec.709 Y′ on encoded sRGB, 0–255. Full-range Cb/Cr vectorscope; no video/legal-range or HDR inference.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 6) });
            top.Children.Add(status);
            var options = new StackPanel { Orientation = Orientation.Horizontal }; options.Children.Add(luma); options.Children.Add(logHistogram); top.Children.Add(options);
            var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
            bottom.Children.Add(clipping);
            bottom.Children.Add(new TextBlock { Text = "Trace density is logarithmic. Fully transparent samples are excluded; partially transparent samples use straight RGB without alpha weighting. Zero/255 counts describe this 8-bit frame, not clipping in the RAW.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            var grid = new Grid(); body.Children.Add(grid);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(.8, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
            Grid.SetColumnSpan(histogram, 2); grid.Children.Add(histogram);
            Grid.SetRow(waveform, 1); grid.Children.Add(waveform);
            Grid.SetRow(vectorscope, 1); Grid.SetColumn(vectorscope, 1); grid.Children.Add(vectorscope);
            luma.Checked += (_, _) => RefreshPlots(); luma.Unchecked += (_, _) => RefreshPlots();
            logHistogram.Checked += (_, _) => RefreshPlots(); logHistogram.Unchecked += (_, _) => RefreshPlots();
            Closed += (_, _) => { IsClosed = true; pending?.Cancel(); lastFrame = null; current = null; };
        }

        internal void Update(BitmapSource frame)
        {
            Dispatcher.VerifyAccess();
            ArgumentNullException.ThrowIfNull(frame);
            if (IsClosed || ReferenceEquals(lastFrame, frame) && frame.IsFrozen) return;
            pending?.Cancel();
            try
            {
                var snapshot = FreezeSnapshot(frame);
                lastFrame = frame;
                var work = new CancellationTokenSource(); pending = work;
                status.Text = current is null ? "Computing scopes…" : "Updating scopes… previous frame shown until ready.";
                PendingUpdate = ComputeAndPublish(snapshot, work);
            }
            catch (Exception error) { lastFrame = null; status.Text = "Scopes unavailable: " + error.GetBaseException().Message; }
        }

        private async Task ComputeAndPublish(BitmapSource frame, CancellationTokenSource work)
        {
            Computed? result = null; Exception? failure = null;
            try { result = await Task.Run(() => Compute(frame, work.Token), work.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { }
            catch (Exception error) { failure = error; }
            if (Dispatcher.HasShutdownStarted) { work.Dispose(); return; }
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsClosed && ReferenceEquals(pending, work) && !work.IsCancellationRequested)
                    {
                        if (result is not null)
                        {
                            current = result;
                            var s = result.Data.Sampling;
                            status.Text = $"{s.SourceWidth:N0} × {s.SourceHeight:N0} frame · " + (s.IsFull ? $"All {s.Count:N0} pixels" : $"Sampled {s.Count:N0} / {(long)s.SourceWidth * s.SourceHeight:N0} pixels on a fixed {s.Width} × {s.Height} grid; sparse clipping may be missed")
                                + $" · {result.Data.TransparentPixels:N0} transparent skipped.";
                            clipping.Text = DescribeClipping(result.Data);
                            RefreshPlots();
                        }
                        else if (failure is not null)
                        {
                            lastFrame = null;
                            status.Text = "Scopes unavailable: " + failure.GetBaseException().Message
                                + (current is null ? "" : " Previous frame remains shown.");
                        }
                    }
                    if (ReferenceEquals(pending, work)) pending = null;
                });
            }
            catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { }
            finally { work.Dispose(); }
        }

        private void RefreshPlots()
        {
            histogram.Set(current?.Data, null, logHistogram.IsChecked == true);
            waveform.Set(current?.Data, luma.IsChecked == true ? current?.LumaWaveform : current?.RgbWaveform, false, luma.IsChecked == true);
            vectorscope.Set(current?.Data, current?.Vector, false);
        }
    }

    internal static string DescribeClipping(ImageScopesMath.Result data)
    {
        string Count(long n) => $"{n:N0} ({(data.VisiblePixels == 0 ? 0 : 100d * n / data.VisiblePixels):0.###}%)";
        return $"Boundary codes among {data.VisiblePixels:N0} visible samples · black RGB=0: {Count(data.BlackPixels)} · white RGB=255: {Count(data.WhitePixels)}\n"
            + $"Any channel at 0: {Count(data.AnyChannelLow)} · at 255: {Count(data.AnyChannelHigh)}\n"
            + $"R at 0/255: {Count(data.Low[0])} / {Count(data.High[0])}    G: {Count(data.Low[1])} / {Count(data.High[1])}    B: {Count(data.Low[2])} / {Count(data.High[2])}";
    }

    private enum PlotKind { Histogram, Waveform, Vector }
    private sealed class ScopePlot(PlotKind kind) : FrameworkElement
    {
        private static readonly Brush Background = FrozenBrush(17, 20, 24);
        private static readonly Brush[] Colors = [FrozenBrush(247, 92, 93), FrozenBrush(102, 222, 130), FrozenBrush(92, 151, 255), FrozenBrush(230, 230, 230)];
        private static readonly Pen GridPen = new(FrozenBrush(54, 60, 68), 1);
        private ImageScopesMath.Result? data;
        private BitmapSource? bitmap;
        private bool logarithmic, luma;
        internal void Set(ImageScopesMath.Result? result, BitmapSource? image, bool log, bool lumaOnly = false)
        { data = result; bitmap = image; logarithmic = log; luma = lumaOnly; InvalidateVisual(); }
        private static Brush FrozenBrush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth < 70 || ActualHeight < 70) return;
            var outer = new Rect(3, 3, ActualWidth - 6, ActualHeight - 6);
            dc.DrawRectangle(Background, null, outer);
            var plot = new Rect(outer.X + 32, outer.Y + 29, outer.Width - 45, outer.Height - 53);
            if (kind == PlotKind.Vector)
            {
                double size = Math.Min(plot.Width, plot.Height);
                plot = new Rect(plot.X + (plot.Width - size) / 2, plot.Y + (plot.Height - size) / 2, size, size);
            }
            Text(dc, kind == PlotKind.Histogram ? "Histogram · R G B Y′" : kind == PlotKind.Waveform ? (luma ? "Waveform · Y′" : "Waveform · RGB") : "Vectorscope · Cb → / Cr ↑", outer.X + 9, outer.Y + 6);
            if (bitmap is not null) dc.DrawImage(bitmap, plot);
            for (int i = 0; i <= 4; ++i)
            {
                double x = plot.Left + plot.Width * i / 4, y = plot.Top + plot.Height * i / 4;
                dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }
            if (kind == PlotKind.Histogram && data is not null) DrawHistogram(dc, plot);
            if (kind == PlotKind.Vector) DrawTargets(dc, plot);
            else
            {
                Text(dc, kind == PlotKind.Waveform ? "255" : "peak", outer.X + 4, plot.Top - 5);
                Text(dc, "0", outer.X + 13, plot.Bottom - 10);
                Text(dc, kind == PlotKind.Histogram ? "0" : "Image left", plot.Left, plot.Bottom + 5);
                Text(dc, kind == PlotKind.Histogram ? "255" : "Image right", plot.Right - (kind == PlotKind.Histogram ? 18 : 53), plot.Bottom + 5);
            }
        }
        private void DrawHistogram(DrawingContext dc, Rect plot)
        {
            double peak = data!.Histogram.Max(x => x.Max());
            if (peak == 0) return;
            double scale = logarithmic ? Math.Log(1 + peak) : peak;
            for (int c = 0; c < 4; ++c)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    for (int i = 0; i < 256; ++i)
                    {
                        double count = data.Histogram[c][i];
                        var point = new Point(plot.Left + plot.Width * i / 255, plot.Bottom - plot.Height * (logarithmic ? Math.Log(1 + count) : count) / scale);
                        if (i == 0) path.BeginFigure(point, false, false); else path.LineTo(point, true, false);
                    }
                }
                geometry.Freeze(); dc.DrawGeometry(null, new Pen(Colors[c], c == 3 ? 1 : 1.4), geometry);
            }
            Text(dc, $"Peak {peak:N0}" + (logarithmic ? " · log" : " · linear"), plot.Right - 140, plot.Top + 4);
        }
        private void DrawTargets(DrawingContext dc, Rect plot)
        {
            Point At(int x, int y) => new(plot.Left + plot.Width * x / 255, plot.Bottom - plot.Height * y / 255);
            var center = At(128, 128);
            dc.DrawEllipse(null, GridPen, center, plot.Width * .25, plot.Height * .25);
            dc.DrawEllipse(null, new Pen(Colors[3], 1), center, 3, 3);
            foreach (var (name, r, g, b) in new[] { ("R",255,0,0), ("Y",255,255,0), ("G",0,255,0), ("C",0,255,255), ("B",0,0,255), ("M",255,0,255) })
            {
                var bin = ImageScopesMath.VectorBin(r, g, b); var at = At(bin.X, bin.Y);
                dc.DrawRectangle(null, new Pen(FrozenBrush((byte)r, (byte)g, (byte)b), 1.5), new Rect(at.X - 3, at.Y - 3, 6, 6));
                Text(dc, name, Math.Clamp(at.X + 6, plot.Left + 3, plot.Right - 12), Math.Clamp(at.Y - 6, plot.Top + 3, plot.Bottom - 13));
            }
            Text(dc, "100% sRGB primaries · full range", plot.Left, plot.Bottom + 5);
        }
        private void Text(DrawingContext dc, string text, double x, double y) => dc.DrawText(
            new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
                Colors[3], VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
}
