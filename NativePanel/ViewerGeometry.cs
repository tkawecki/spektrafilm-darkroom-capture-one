using System;
using System.Drawing;
using System.Windows;
using System.Windows.Media;

internal readonly record struct ViewerGeometry(Rect Clip, Matrix Transform)
{
    internal static bool TryCreate(RectangleF clip, ReadOnlySpan<PointF> corners, int width, int height,
        out ViewerGeometry geometry)
    {
        geometry = default;
        if (corners.Length != 4 || width <= 0 || height <= 0
            || !float.IsFinite(clip.Left) || !float.IsFinite(clip.Top)
            || !float.IsFinite(clip.Right) || !float.IsFinite(clip.Bottom)
            || clip.Width <= 0 || clip.Height <= 0)
            return false;

        foreach (var point in corners)
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                return false;

        var origin = corners[0];
        var x = new Vector((double)corners[1].X - origin.X, (double)corners[1].Y - origin.Y);
        var y = new Vector((double)corners[3].X - origin.X, (double)corners[3].Y - origin.Y);
        var expected = new System.Windows.Point(origin.X + x.X + y.X, origin.Y + x.Y + y.Y);
        // The native corners are single-precision physical pixels. Accept subpixel
        // rounding, but require the fourth corner to close the same parallelogram.
        var tolerance = Math.Max(0.5, Math.Max(x.Length, y.Length) * 0.00001);
        if (Math.Abs(expected.X - corners[2].X) > tolerance || Math.Abs(expected.Y - corners[2].Y) > tolerance)
            return false;

        // A frame with less than one column or row in the Viewer cannot be
        // displayed usefully. The area test also rejects near-collinear edges.
        var area = Math.Abs(Vector.CrossProduct(x, y));
        if (area < Math.Max(x.Length, y.Length) || area == 0)
            return false;

        var minX = Math.Min(Math.Min(corners[0].X, corners[1].X), Math.Min(corners[2].X, corners[3].X));
        var minY = Math.Min(Math.Min(corners[0].Y, corners[1].Y), Math.Min(corners[2].Y, corners[3].Y));
        var maxX = Math.Max(Math.Max(corners[0].X, corners[1].X), Math.Max(corners[2].X, corners[3].X));
        var maxY = Math.Max(Math.Max(corners[0].Y, corners[1].Y), Math.Max(corners[2].Y, corners[3].Y));
        if (maxX <= clip.Left || minX >= clip.Right || maxY <= clip.Top || minY >= clip.Bottom)
            return false;

        var matrix = new Matrix(x.X / width, x.Y / width, y.X / height, y.Y / height, origin.X, origin.Y);
        if (!matrix.HasInverse)
            return false;
        geometry = new ViewerGeometry(new Rect(clip.Left, clip.Top, clip.Width, clip.Height), matrix);
        return true;
    }
}
