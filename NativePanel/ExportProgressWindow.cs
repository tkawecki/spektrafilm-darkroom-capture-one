using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

// A modeless child of the native modal Export dialog remains usable while
// that dialog disables the main C1 window. It never owns export work itself.
internal static class ExportProgressWindow
{
    internal static IDisposable Show(Window? owner, TextBlock statusSource, Action cancel)
    {
        var progress = new ProgressWindow(owner, statusSource, cancel);
        try { progress.Show(); return progress; }
        catch { progress.Dispose(); throw; }
    }

    internal sealed class ProgressWindow : Window, IDisposable
    {
        private readonly Window? watchedOwner;
        private readonly Action cancel;
        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSalmon };
        private readonly Button cancelButton = new() { Content = "Cancel export", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right };
        private bool completed, cancellationRequested, closed, detached;

        internal ProgressWindow(Window? owner, TextBlock statusSource, Action cancel)
        {
            ArgumentNullException.ThrowIfNull(statusSource);
            this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
            watchedOwner = owner;
            Title = "Spektrafilm export";
            Width = 480; MinWidth = 340; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = owner is null;
            ShowActivated = owner is { IsVisible: true };
            Background = new SolidColorBrush(Color.FromRgb(31, 36, 43)); Foreground = Brushes.Gainsboro;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (owner is { IsVisible: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(status); body.Children.Add(error); body.Children.Add(cancelButton); Content = body;
            BindingOperations.SetBinding(status, TextBlock.TextProperty, new Binding(nameof(TextBlock.Text))
            { Source = statusSource, Mode = BindingMode.OneWay });
            cancelButton.Click += (_, _) => RequestCancellation();
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            Closing += OnClosing;
            Closed += OnClosed;
            if (watchedOwner is not null) watchedOwner.Closed += OnOwnerClosed;
        }

        private void RequestCancellation()
        {
            if (completed || cancellationRequested) return;
            cancellationRequested = true;
            cancelButton.IsEnabled = false; cancelButton.Content = "Cancelling…";
            try { cancel(); }
            catch (ObjectDisposedException) { } // The owning export may have just completed.
            catch (Exception failure)
            {
                error.Text = "Could not cancel export: " + failure.GetBaseException().Message;
                Debug.WriteLine(failure);
            }
        }

        private void OnClosing(object? sender, CancelEventArgs e) => RequestCancellation();
        private void OnClosed(object? sender, EventArgs e)
        {
            closed = true;
            // WPF can close an owned window without raising its Closing event.
            RequestCancellation();
            Detach();
        }
        private void OnOwnerClosed(object? sender, EventArgs e)
        {
            RequestCancellation();
            if (!closed) Close();
        }
        private void Detach()
        {
            if (detached) return;
            detached = true;
            if (watchedOwner is not null) watchedOwner.Closed -= OnOwnerClosed;
            BindingOperations.ClearBinding(status, TextBlock.TextProperty);
        }

        public void Dispose()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.Invoke(Dispose);
                return;
            }
            if (completed) return;
            completed = true;
            Detach();
            if (!closed) Close();
        }
    }
}
