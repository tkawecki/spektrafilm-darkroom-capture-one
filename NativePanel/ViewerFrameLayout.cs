using System;
using System.Drawing;
using System.Windows;

internal sealed record ViewerFrameLayout(int CanvasWidth, int CanvasHeight, Rectangle Crop)
{
    internal Rect Fit(int imageWidth, int imageHeight)
    {
        if (imageWidth < 1 || imageHeight < 1 || CanvasWidth < 1 || CanvasHeight < 1 || Crop.Width < 1 || Crop.Height < 1
            || Crop.Left < 0 || Crop.Top < 0 || Crop.Right > CanvasWidth || Crop.Bottom > CanvasHeight)
            throw new ArgumentException("Invalid post-scan Viewer layout.");
        double scale = Math.Min(Crop.Width / (double)imageWidth, Crop.Height / (double)imageHeight);
        double width = imageWidth * scale, height = imageHeight * scale;
        return new(Crop.Left + (Crop.Width-width)/2, Crop.Top + (Crop.Height-height)/2, width, height);
    }
}
