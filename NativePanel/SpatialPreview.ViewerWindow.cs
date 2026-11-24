using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Zoomed-in Viewer window (2026-11-23). When less than half of the photo is visible, the native Viewer frame is
// not rendered whole: the renderer renders only the visible part with a margin (OpticalRenderer.RenderViewAsync,
// VIEW8), taking every full-frame step (auto exposure, highlight boost, spatial context, grain positions) from the
// whole frame, so the window equals the same pixels of a full frame (tests/ViewerWindowTests). The window is drawn
// over the last frame (the base, at most 2560 px in Auto); after a settings change the base is refreshed under the
// window. A pan beyond the margin renders a new window once the Viewer rests.
internal sealed partial class SpatialPreview
{
    private const double WindowAreaLimit = 0.5;   // window when less than this fraction of the photo is visible
    private const double WindowMargin = 0.25;     // around the visible part, as a fraction of its width and height
    private const long WindowPanSettleMs = 150;
    private const double WindowAreaMax = 0.6;     // a larger window is rendered as a whole frame instead
    // The window renderer keeps a second copy of the source (CPU and GPU); larger sources render whole frames.
    private const long WindowSourcePixelsMax = 64_000_000;
    private Int32Rect? windowRect;                // in source pixels
    private string[]? windowArguments;
    private double windowStrength;
    private HostImageSource.Frame? windowSource;
    private long windowNeededSince;
    // The base under the window: arguments, strength and source it was rendered with.
    private string[]? baseArguments;
    private double baseStrength;
    private HostImageSource.Frame? baseSource;
    // A renderer of its own: it keeps the whole source resident, while the main renderer renders the base at a
    // smaller size (one renderer for both would upload the whole source again for every window).
    private OpticalRenderer? windowRenderer;

    private HostImageSource.Frame? prefetchedSource;

    // Uploads the whole source to the window renderer in the background (a 1 × 1 px window), once per source.
    private void PrefetchWindowSource(HostImageSource.Frame full, string[] args)
    {
        if (ReferenceEquals(prefetchedSource, full))
            return;
        prefetchedSource = full;
        var active = windowRenderer ??= new OpticalRenderer(false, true);
        _ = Task.Run(async () =>
        {
            try
            {
                await active.RenderViewAsync(full.Width, full.Height, full.FloatBytes, full.Width / 2, full.Height / 2, 1, 1,
                    args, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                StartupHook.Log("Spektrafilm Viewer window prefetch failed: " + error.GetBaseException().Message);
            }
        });
    }

    private void ReleaseWindowRenderer()
    {
        windowRenderer?.Dispose();
        windowRenderer = null;
        prefetchedSource = null;
        ForgetWindow();
    }

    private bool WindowModelAllowed()
        => !(choices.TryGetValue("grain-model", out var grain) && grain.SelectedItem is Choice { Id: "synthesis" });

    // The visible part of the photo in source pixels; null when unknown or not for this photo.
    private Int32Rect? VisibleSourceRect(HostImageSource.Frame full)
    {
        if (viewer.PaintedVariant != activeVariant || viewer.VisibleRegion is not { IsEmpty: false } visible)
            return null;
        var x0 = (int)Math.Floor(visible.Left * full.Width);
        var y0 = (int)Math.Floor(visible.Top * full.Height);
        var x1 = (int)Math.Ceiling(visible.Right * full.Width);
        var y1 = (int)Math.Ceiling(visible.Bottom * full.Height);
        x0 = Math.Clamp(x0, 0, full.Width - 1);
        y0 = Math.Clamp(y0, 0, full.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, full.Width);
        y1 = Math.Clamp(y1, y0 + 1, full.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // The window to render for the current zoom and pan; null when most of the photo is visible.
    private Int32Rect? WantedWindow(HostImageSource.Frame full)
    {
        if ((long)full.Width * full.Height > WindowSourcePixelsMax
            || viewer.VisibleRegion is not { IsEmpty: false } visible || visible.Width * visible.Height >= WindowAreaLimit
            || VisibleSourceRect(full) is not { } shown)
            return null;
        var marginX = (int)Math.Ceiling(shown.Width * WindowMargin);
        var marginY = (int)Math.Ceiling(shown.Height * WindowMargin);
        var x0 = Math.Max(0, shown.X - marginX);
        var y0 = Math.Max(0, shown.Y - marginY);
        var x1 = Math.Min(full.Width, shown.X + shown.Width + marginX);
        var y1 = Math.Min(full.Height, shown.Y + shown.Height + marginY);
        if ((double)(x1 - x0) * (y1 - y0) > WindowAreaMax * full.Width * full.Height)
            return null;
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // The window on screen still holds the visible part of this source.
    private bool WindowCovers(HostImageSource.Frame full)
    {
        if (windowRect is not { } held || !ReferenceEquals(windowSource, full) || VisibleSourceRect(full) is not { } shown)
            return false;
        return shown.X >= held.X && shown.Y >= held.Y && shown.X + shown.Width <= held.X + held.Width
            && shown.Y + shown.Height <= held.Y + held.Height;
    }

    // Removes the window from the Viewer too.
    private void ForgetWindow()
    {
        if (windowRect is not null && activeVariant is { } shownVariant)
            viewer.ShowWindow(shownVariant, null, Rect.Empty);
        windowRect = null;
        windowArguments = null;
        windowSource = null;
        windowNeededSince = 0;
    }

    // Pan beyond the window's margin: render a new window once the Viewer has rested for a moment.
    private bool WindowPanSettled(HostImageSource.Frame full)
    {
        if (windowRect is null || WindowCovers(full) || WantedWindow(full) is null)
        {
            windowNeededSince = 0;
            return false;
        }
        var now = Environment.TickCount64;
        if (windowNeededSince == 0)
        {
            windowNeededSince = now;
            return false;
        }
        if (now - windowNeededSince < WindowPanSettleMs)
            return false;
        windowNeededSince = 0;
        return true;
    }

    // The base under the window shows the current settings of this source.
    private bool WindowBaseCurrent(string[] args, double strength, HostImageSource.Frame full)
        => ReferenceEquals(baseSource, full) && baseArguments is { } held && held.SequenceEqual(args) && baseStrength == strength;

    private void RememberBase(string[] args, double strength, HostImageSource.Frame full)
    {
        baseArguments = args;
        baseStrength = strength;
        baseSource = full;
    }

    // Base under a window: the largest Auto step, so the frame around the window stays light to render.
    private static int WindowBaseLongEdge(HostImageSource.Frame full)
    {
        var edge = SettleLongEdges[^1];
        return (long)edge * 4 <= (long)Math.Max(full.Width, full.Height) * 3 ? edge : 0;
    }

    // The window renderer already holds this source and windows are allowed (a draft must not wait for an upload).
    private bool WindowDraftReady(HostImageSource.Frame full)
        => windowRenderer is not null && ReferenceEquals(prefetchedSource, full) && WindowModelAllowed();

    // Interactive preview while a control is dragged at high zoom: the window with the final settings, in place of
    // the 1600 px draft of the whole photo. The final job then finds the window current and only refreshes the base.
    private async Task RefreshWindowDraftAsync(Guid selectedVariant, HostImageSource.Revision revision, HostImageSource.Frame input,
        InputChoice inputColor, Choice encoding, LookSettings look, string viewerSize, Int32Rect wanted)
    {
        var settingsVersion = inputRevision;
        lastDraftRevision = settingsVersion;
        var started = Stopwatch.GetTimestamp();
        using var job = new CancellationTokenSource();
        pendingDraft = job;
        try
        {
            var args = RendererOptions(look, inputColor.Id, encoding.Id);
            if (await RenderViewerWindowAsync(input, wanted, args, look.Strength, job.Token) is not { } bitmap)
                return;
            job.Token.ThrowIfCancellationRequested();
            // As for drafts: the newest finished window is shown even when a later change has arrived.
            if (frameCurrent || !WantsQuickPreview || exportWork is not null || activeVariant != selectedVariant
                || !ReferenceEquals(source, input) || !ReferenceEquals(inputSpace.SelectedItem, inputColor)
                || !ReferenceEquals(outputSpace.SelectedItem, encoding) || ((Choice)previewSize.SelectedItem).Id != viewerSize
                || HostImageSource.GetRevision(window, selectedVariant) != revision)
                return;
            PresentViewerWindow(selectedVariant, input, wanted, bitmap, args, look.Strength, started);
            previewDetail = "Interactive preview";
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (!WantsQuickPreview || selectionUnavailable) { }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm window preview: " + error.GetBaseException().Message);
        }
        finally
        {
            pendingDraft = null;
            if (enabled && lastDraftRevision != inputRevision && WantsQuickPreview)
                _ = refresh.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RefreshAsync()));
        }
    }

    // Renders the window; null when it could not be rendered (the caller renders a whole frame instead).
    private async Task<BitmapSource?> RenderViewerWindowAsync(HostImageSource.Frame input, Int32Rect wanted, string[] args,
        double strength, CancellationToken token)
    {
        try
        {
            var active = windowRenderer ??= new OpticalRenderer(false, true);
            var packed = await active.RenderViewAsync(input.Width, input.Height, input.FloatBytes, wanted.X, wanted.Y,
                wanted.Width, wanted.Height, WithDisplayStrength(args, strength), token);
            token.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                var image = BitmapSource.Create(wanted.Width, wanted.Height, 96, 96, PixelFormats.Bgra32, null, packed, wanted.Width * 4);
                image.Freeze();
                return ForViewer(image, token);
            }, token);
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // A failed window (for example GPU memory) must not cost the frame on screen: render a whole frame.
            StartupHook.Log("Spektrafilm Viewer window failed; rendering the whole frame instead: " + error.GetBaseException().Message);
            ReleaseWindowRenderer();
            return null;
        }
    }

    private void PresentViewerWindow(Guid variant, HostImageSource.Frame input, Int32Rect wanted, BitmapSource bitmap,
        string[] args, double strength, long started)
    {
        var region = new Rect((double)wanted.X / input.Width, (double)wanted.Y / input.Height,
            (double)wanted.Width / input.Width, (double)wanted.Height / input.Height);
        viewer.ShowWindow(variant, bitmap, region);
        windowRect = wanted;
        windowArguments = args;
        windowStrength = strength;
        windowSource = input;
        StartupHook.Log($"Live Viewer window {wanted.Width} × {wanted.Height} of {input.Width} × {input.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms. {DescribeRender(args)}");
    }
}
