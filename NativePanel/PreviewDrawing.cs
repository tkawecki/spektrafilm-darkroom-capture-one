using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PreviewDrawing
{
    internal static DrawingGroup Create(BitmapSource bitmap)
    {
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
        using (var context = group.Open())
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        group.Freeze();
        return group;
    }
}
