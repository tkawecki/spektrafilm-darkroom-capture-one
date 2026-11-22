using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed partial class SpatialPreview
{
    private ScanGeometrySettings CaptureGeometrySettings() => new(
        Active: toggles["finishing-enabled"].IsChecked == true,
        Crop: toggles["finishing-crop"].IsChecked == true,
        CenterX: values["finishingCropCenterX"].Value, CenterY: values["finishingCropCenterY"].Value,
        SizeX: values["finishingCropSizeX"].Value, SizeY: values["finishingCropSizeY"].Value,
        SkewDegrees: values["finishingSkewDeg"].Value, Desqueeze: EffectiveDesqueeze(values["finishingDesqueeze"].Value));

    // Desqueeze is limited to |v| >= 1 (user decision 2026-11-17); 0 and ±1 are identity. Values
    // in between, also from older saved looks, mean no desqueeze.
    internal static double EffectiveDesqueeze(double value) => Math.Abs(value) < 1 ? 1 : value;
    internal static double SnapDesqueeze(double value) =>
        Math.Abs(value) >= 1 ? value : Math.Abs(value) < 0.5 ? 0 : Math.Sign(value);

    private static ViewerFrameLayout GeometryLayout(HostImageSource.Frame frame)
    {
        if (frame.PreviewCropError is { } error) throw new InvalidDataException(error);
        return new(frame.Width, frame.Height, frame.PreviewCrop ?? new(0, 0, frame.Width, frame.Height));
    }

    private static BitmapSource DecodeGeometryPreview(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace input, string encoding, CancellationToken stop, FinishingTransform finishing, ScanGeometrySettings geometry)
    {
        if (geometry.IsIdentity) return DecodeResultWithFinishing(frame, rendered, strength, input, encoding, stop, finishing);
        var layout = GeometryLayout(frame);
        var rgba = DecodeFinishedFloat(frame, rendered, strength, input, encoding, layout.Crop, stop, finishing, ExportRgbSpace.Srgb);
        var result = ScanGeometry.ApplyRgba(rgba, layout.Crop.Width, layout.Crop.Height, geometry, stop);
        var bytes = new byte[result.Pixels.Length]; var pixels = result.Pixels.Span;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            if ((i & 16383) == 0) stop.ThrowIfCancellationRequested();
            bytes[i] = (byte)Math.Round(Math.Clamp((double)pixels[i+2], 0, 1) * 255);
            bytes[i+1] = (byte)Math.Round(Math.Clamp((double)pixels[i+1], 0, 1) * 255);
            bytes[i+2] = (byte)Math.Round(Math.Clamp((double)pixels[i], 0, 1) * 255);
            bytes[i+3] = (byte)Math.Round(Math.Clamp((double)pixels[i+3], 0, 1) * 255);
        }
        var bitmap = BitmapSource.Create(result.Width, result.Height, 96, 96, PixelFormats.Bgra32, null, bytes, result.Width*4);
        bitmap.Freeze(); return bitmap;
    }
}
