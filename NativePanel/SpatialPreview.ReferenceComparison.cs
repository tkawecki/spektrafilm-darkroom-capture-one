using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

internal sealed partial class SpatialPreview
{
    private BitmapSource? pinnedReference;
    private string pinnedReferenceLabel = "";
    private ReferenceComparison? referenceComparison;
    internal void PinReference()
    {
        if (!enabled || !frameCurrent || viewerFrame is not { } frame)
            throw new InvalidOperationException("Wait for the full Viewer frame before pinning a reference.");
        var target = HostProfile.CaptureTarget(window);
        if (target.VariantId != activeVariant) throw new InvalidOperationException("Wait for the selected photo to finish rendering.");
        pinnedReference = frame;
        pinnedReferenceLabel = Path.GetFileName(target.SourcePath) + " · " + DateTime.Now.ToString("HH:mm:ss");
        referenceComparison?.Close(); referenceComparison = null;
        status.Text = "Reference pinned: " + pinnedReferenceLabel + ". Select another photo or change the look, then compare.";
    }
    internal void ShowReferenceComparison()
    {
        if (pinnedReference is null) throw new InvalidOperationException("Pin a reference frame first.");
        if (!enabled || !frameCurrent || viewerFrame is not { } frame)
            throw new InvalidOperationException("Wait for the current Viewer frame before comparing.");
        if (referenceComparison is { IsClosed: false }) { referenceComparison.Update(frame); referenceComparison.Activate(); return; }
        referenceComparison = new ReferenceComparison(Window.GetWindow(LiveControls), pinnedReference, pinnedReferenceLabel, frame);
        referenceComparison.Closed += (_, _) => referenceComparison = null;
        referenceComparison.Show();
    }
    internal void ClearReference()
    {
        referenceComparison?.Close(); referenceComparison = null;
        pinnedReference = null; pinnedReferenceLabel = "";
    }
}
