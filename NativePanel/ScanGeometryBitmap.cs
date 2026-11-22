using System;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed record ScanGeometryBitmapResult(BitmapSource Bitmap, ScanGeometryPlan Geometry);

// Display-only convenience adapter. High-bit-depth exports must call
// ScanGeometry.ApplyRgba before packing, and attach their target ICC afterward.
internal static class ScanGeometryBitmap
{
    internal static ScanGeometryBitmapResult Apply(BitmapSource source, ScanGeometrySettings settings, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(source); stop.ThrowIfCancellationRequested();
        if (source.Format != PixelFormats.Bgra32) throw new ArgumentException("The display geometry adapter requires unpremultiplied BGRA32.");
        var plan = ScanGeometry.Plan(source.PixelWidth, source.PixelHeight, settings);
        if (settings.IsIdentity) return new(source, plan);
        int width = source.PixelWidth, height = source.PixelHeight;
        var bytes = new byte[checked(width * height * 4)]; source.CopyPixels(bytes, checked(width * 4), 0);
        var pixels = new float[bytes.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if ((i & 16383) == 0) stop.ThrowIfCancellationRequested();
            pixels[i] = bytes[i + 2] / 255f; pixels[i + 1] = bytes[i + 1] / 255f;
            pixels[i + 2] = bytes[i] / 255f; pixels[i + 3] = bytes[i + 3] / 255f;
        }
        var transformed = ScanGeometry.ApplyRgba(pixels, width, height, settings, stop);
        bytes = new byte[transformed.Pixels.Length]; var output = transformed.Pixels.Span;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            if ((i & 16383) == 0) stop.ThrowIfCancellationRequested();
            bytes[i] = (byte)Math.Round(Math.Clamp((double)output[i + 2], 0, 1) * 255);
            bytes[i + 1] = (byte)Math.Round(Math.Clamp((double)output[i + 1], 0, 1) * 255);
            bytes[i + 2] = (byte)Math.Round(Math.Clamp((double)output[i], 0, 1) * 255);
            bytes[i + 3] = (byte)Math.Round(Math.Clamp((double)output[i + 3], 0, 1) * 255);
        }
        var bitmap = BitmapSource.Create(transformed.Width, transformed.Height, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, bytes, checked(transformed.Width * 4));
        bitmap.Freeze(); return new(bitmap, transformed.Geometry);
    }
}
