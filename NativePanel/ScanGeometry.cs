// SPDX-License-Identifier: GPL-3.0-only
// Post-scan order and crop convention: The Dyeworks 2.2.3 scan_finishing.py /
// crop_resize.py (Celian Le Bolloch; spektrafilm by Andrea Volpato).
// C# adaptation 2026-11-16: bounded float RGBA buffers, double resampling,
// independent alpha, cancellation, exact neutral bypass and coordinate plan.
// See THIRD-PARTY.md.
using System;
using System.Threading;
using System.Threading.Tasks;

internal sealed record ScanGeometrySettings(bool Active = true, bool Crop = false,
    double CenterX = .5, double CenterY = .5, double SizeX = 1, double SizeY = 1,
    double SkewDegrees = 0, double Desqueeze = 1)
{
    internal bool IsIdentity => !Active || (!Crop && SkewDegrees == 0 && Desqueeze is 0 or 1 or -1);
    internal void Validate()
    {
        static void Range(double x, double lo, double hi, string name)
        { if (!double.IsFinite(x) || x < lo || x > hi) throw new ArgumentOutOfRangeException(name); }
        Range(CenterX, 0, 1, nameof(CenterX)); Range(CenterY, 0, 1, nameof(CenterY));
        Range(SizeX, .005, 1, nameof(SizeX)); Range(SizeY, .005, 1, nameof(SizeY));
        Range(SkewDegrees, -45, 45, nameof(SkewDegrees)); Range(Desqueeze, -2, 2, nameof(Desqueeze));
    }
}

internal sealed record ScanGeometryPlan(int InputWidth, int InputHeight, int DesqueezedWidth,
    int DesqueezedHeight, int CropX, int CropY, int Width, int Height,
    bool Resample, double SkewDegrees)
{
    // Pixel-centre coordinates, relative to the supplied (already C1-cropped)
    // input. A caller mapping to the full C1 canvas adds its crop origin.
    internal (double X, double Y) MapOutputToInput(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("A pixel position must be finite.");
        x += CropX; y += CropY;
        if (SkewDegrees != 0)
        {
            double angle = SkewDegrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double cx = (DesqueezedWidth - 1) / 2.0, cy = (DesqueezedHeight - 1) / 2.0;
            double dx = x - cx, dy = y - cy;
            x = cx + cos * dx - sin * dy; y = cy + sin * dx + cos * dy;
        }
        return ((x + .5) * InputWidth / DesqueezedWidth - .5,
            (y + .5) * InputHeight / DesqueezedHeight - .5);
    }
}

internal sealed record ScanGeometryResult(ReadOnlyMemory<float> Pixels, ScanGeometryPlan Geometry)
{
    internal int Width => Geometry.Width;
    internal int Height => Geometry.Height;
}

internal static class ScanGeometry
{
    internal const int MaximumEdge = 16384;
    internal const long MaximumPixels = 100_000_000;
    private const double Pole = -.267949192431122706472553658494127633;

    internal static ScanGeometryPlan Plan(int width, int height, ScanGeometrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); Size(width, height);
        if (settings.IsIdentity) return new(width, height, width, height, 0, 0, width, height, false, 0);
        bool resample = settings.Desqueeze is not (0 or 1 or -1);
        int w = width, h = height;
        if (resample)
        {
            if (settings.Desqueeze > 0) w = Math.Max(1, checked((int)Math.Round(width * settings.Desqueeze)));
            else h = Math.Max(1, checked((int)Math.Round(height * -settings.Desqueeze)));
            Size(w, h);
        }
        int x = 0, y = 0, cw = w, ch = h;
        if (settings.Crop)
        {
            cw = Math.Min(w, (int)Math.Round(Math.Max(w, h) * settings.SizeX));
            ch = Math.Min(h, (int)Math.Round(Math.Max(w, h) * settings.SizeY));
            if (cw == 0 || ch == 0) throw new ArgumentException("The scan crop rounds to an empty image at this resolution.");
            x = Math.Clamp((int)Math.Round(Math.Round(w * settings.CenterX) - cw / 2.0), 0, w - cw);
            y = Math.Clamp((int)Math.Round(Math.Round(h * settings.CenterY) - ch / 2.0), 0, h - ch);
        }
        return new(width, height, w, h, x, y, cw, ch, resample, settings.SkewDegrees);
    }

    // Input is the FINISHED output colour space/encoding, before integer
    // packing. No colour conversion is performed. In DW, encoded exports are
    // interpolated encoded; linear exports (including DNG) are interpolated linear.
    internal static ScanGeometryResult ApplyRgba(ReadOnlyMemory<float> pixels, int width, int height,
        ScanGeometrySettings settings, CancellationToken stop = default)
    {
        stop.ThrowIfCancellationRequested();
        var plan = Plan(width, height, settings);
        if ((long)width * height * 4 != pixels.Length) throw new ArgumentException("Geometry needs a complete RGBA frame.");
        if (settings.IsIdentity) return new(pixels, plan); // same memory, no pixel reads or buffer allocations
        var bounds = ReadBounds(pixels, stop);
        var current = pixels;
        if (plan.Resample)
        {
            current = Desqueeze(current, plan, settings.Desqueeze > 0, bounds, stop);
            if (plan.SkewDegrees != 0) bounds = ReadBounds(current, stop);
        }
        if (plan.SkewDegrees != 0) return new(SkewCrop(current, plan, bounds, stop), plan);
        return new(Crop(current, plan.DesqueezedWidth, plan.DesqueezedHeight, plan, stop), plan);
    }

    private static void Size(int w, int h)
    {
        if (w < 1 || h < 1 || w > MaximumEdge || h > MaximumEdge || (long)w * h > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(w), "Scan geometry supports up to 100 MP and 16,384 pixels per side, including intermediate dimensions.");
    }
    private readonly record struct Bounds(double Min, double Max, double AlphaMin, double AlphaMax);
    private static Bounds ReadBounds(ReadOnlyMemory<float> memory, CancellationToken stop)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity, amin = double.PositiveInfinity, amax = double.NegativeInfinity;
        var pixels = memory.Span;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 16383) == 0) stop.ThrowIfCancellationRequested();
            double v = pixels[i]; if (!double.IsFinite(v)) throw new ArgumentException("Scan geometry requires finite RGBA values.");
            if ((i & 3) == 3)
            {
                if (v < 0 || v > 1) throw new ArgumentException("Scan geometry alpha must be between 0 and 1.");
                amin = Math.Min(amin, v); amax = Math.Max(amax, v);
            }
            else { min = Math.Min(min, v); max = Math.Max(max, v); }
        }
        return new(min, max, amin, amax);
    }
    private static int Mirror(int i, int n)
    {
        if (n == 1) return 0;
        int period = 2 * n - 2, j = Math.Abs(i % period);
        return j < n ? j : period - j;
    }
    private sealed class LineBuffer(int size)
    {
        internal readonly double[] Values = new double[size], Coefficients = new double[size];
    }
    private static float[] Desqueeze(ReadOnlyMemory<float> input, ScanGeometryPlan p, bool horizontal, Bounds bounds, CancellationToken stop)
    {
        int n = horizontal ? p.InputWidth : p.InputHeight, count = horizontal ? p.DesqueezedWidth : p.DesqueezedHeight;
        int lines = horizontal ? p.InputHeight : p.InputWidth;
        // Rescale derives sigma and sample positions from the rounded output
        // dimensions, not the requested factor.
        double sigma = Math.Max(0, (n / (double)count - 1) / 2);
        int radius = sigma > 1e-15 ? (int)(4 * sigma + .5) : 0;
        var weights = new double[radius * 2 + 1]; double sum = 0;
        for (int k = -radius; k <= radius; k++) sum += weights[k + radius] = radius == 0 ? 1 : Math.Exp(-.5 / (sigma * sigma) * k * k);
        for (int k = 0; k < weights.Length; k++) weights[k] /= sum;
        var output = new float[checked(p.DesqueezedWidth * p.DesqueezedHeight * 4)];
        Parallel.For(0, lines, new ParallelOptions { CancellationToken = stop }, () => new LineBuffer(n), (line, _, buffer) =>
        {
            var source = input.Span;
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i < n; i++) buffer.Values[i] = source[(horizontal ? line * n + i : i * p.InputWidth + line) * 4 + c];
                if (radius == 0) buffer.Values.CopyTo(buffer.Coefficients, 0);
                else for (int i = 0; i < n; i++)
                {
                    if ((i & 255) == 0) stop.ThrowIfCancellationRequested();
                    double v = 0; for (int k = -radius; k <= radius; k++) v += weights[k + radius] * buffer.Values[Mirror(i + k, n)];
                    buffer.Coefficients[i] = v;
                }
                Prefilter(buffer.Coefficients);
                // Alpha is a C1 extension (DW is RGB): keep the same kernel,
                // bounded to coverage [0,1], independently of RGB's range.
                double lo = c == 3 ? 0 : bounds.Min, hi = c == 3 ? 1 : bounds.Max;
                for (int o = 0; o < count; o++)
                {
                    double x = (o + .5) * n / count - .5;
                    int i = (int)Math.Floor(x); double t = x - i, t2 = t * t, t3 = t2 * t;
                    double v = ((1 - t) * (1 - t) * (1 - t) * buffer.Coefficients[Mirror(i - 1, n)]
                        + (4 - 6 * t2 + 3 * t3) * buffer.Coefficients[Mirror(i, n)]
                        + (1 + 3 * t + 3 * t2 - 3 * t3) * buffer.Coefficients[Mirror(i + 1, n)]
                        + t3 * buffer.Coefficients[Mirror(i + 2, n)]) / 6;
                    output[(horizontal ? line * count + o : o * p.DesqueezedWidth + line) * 4 + c] = (float)Math.Clamp(v, lo, hi);
                }
            }
            return buffer;
        }, _ => { });
        return output;
    }
    private static void Prefilter(double[] c)
    {
        int n = c.Length; if (n == 1) return;
        for (int i = 0; i < n; i++) c[i] *= 6;
        double zn = Math.Pow(Pole, n - 1), z = Pole, first = c[0] + zn * c[n - 1];
        for (int i = 1; i < n - 1; i++) { first += z * (c[i] + zn * c[n - 1 - i]); z *= Pole; }
        c[0] = first / (1 - zn * zn);
        for (int i = 1; i < n; i++) c[i] += Pole * c[i - 1];
        c[n - 1] = (Pole * c[n - 2] + c[n - 1]) * Pole / (Pole * Pole - 1);
        for (int i = n - 2; i >= 0; i--) c[i] = Pole * (c[i + 1] - c[i]);
    }
    private static ReadOnlyMemory<float> Crop(ReadOnlyMemory<float> input, int width, int height, ScanGeometryPlan p, CancellationToken stop)
    {
        if (p.CropX == 0 && p.Width == width) return input.Slice(p.CropY * width * 4, p.Height * width * 4);
        var output = new float[checked(p.Width * p.Height * 4)];
        for (int y = 0; y < p.Height; y++)
        {
            stop.ThrowIfCancellationRequested();
            input.Span.Slice(((y + p.CropY) * width + p.CropX) * 4, p.Width * 4).CopyTo(output.AsSpan(y * p.Width * 4));
        }
        return output;
    }
    private sealed class OutputBounds
    {
        internal double Min = double.PositiveInfinity, Max = double.NegativeInfinity, AlphaMin = double.PositiveInfinity, AlphaMax = double.NegativeInfinity;
    }
    private static float[] SkewCrop(ReadOnlyMemory<float> input, ScanGeometryPlan p, Bounds bounds, CancellationToken stop)
    {
        int width = p.DesqueezedWidth, height = p.DesqueezedHeight;
        double angle = p.SkewDegrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        double tx = cx - cos * cx + sin * cy, ty = cy - sin * cx - cos * cy;
        var output = new float[checked(p.Width * p.Height * 4)];
        var total = new OutputBounds();
        // Evaluate the entire skew canvas for skimage's global clip/cval rule,
        // while storing only the final crop (no second full-size skew buffer).
        Parallel.For(0, height, new ParallelOptions { CancellationToken = stop }, () => new OutputBounds(), (y, _, local) =>
        {
            var source = input.Span;
            for (int x = 0; x < width; x++)
            {
                double sx = cos * x - sin * y + tx, sy = sin * x + cos * y + ty;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy); double fx = sx - ix, fy = sy - iy;
                bool save = x >= p.CropX && x < p.CropX + p.Width && y >= p.CropY && y < p.CropY + p.Height;
                for (int c = 0; c < 4; c++)
                {
                    double top = Sample(source, width, height, ix, iy, c) * (1 - fx) + Sample(source, width, height, ix + 1, iy, c) * fx;
                    double bottom = Sample(source, width, height, ix, iy + 1, c) * (1 - fx) + Sample(source, width, height, ix + 1, iy + 1, c) * fx;
                    double v = top * (1 - fy) + bottom * fy;
                    if (c == 3) { local.AlphaMin = Math.Min(local.AlphaMin, v); local.AlphaMax = Math.Max(local.AlphaMax, v); }
                    else { local.Min = Math.Min(local.Min, v); local.Max = Math.Max(local.Max, v); }
                    if (save) output[((y - p.CropY) * p.Width + x - p.CropX) * 4 + c] = (float)v;
                }
            }
            return local;
        }, local => { lock (total) { total.Min = Math.Min(total.Min, local.Min); total.Max = Math.Max(total.Max, local.Max); total.AlphaMin = Math.Min(total.AlphaMin, local.AlphaMin); total.AlphaMax = Math.Max(total.AlphaMax, local.AlphaMax); } });
        static (double Lo, double Hi) Clip(double lo, double hi, double outputLo, double outputHi) =>
            !(lo <= 1 && 1 <= hi) && outputLo <= 1 && 1 <= outputHi ? (Math.Min(lo, 1), Math.Max(hi, 1)) : (lo, hi);
        var rgb = Clip(bounds.Min, bounds.Max, total.Min, total.Max);
        for (int i = 0; i < output.Length; i++)
        {
            if ((i & 16383) == 0) stop.ThrowIfCancellationRequested();
            output[i] = (float)((i & 3) == 3 ? Math.Clamp(output[i], 0, 1) : Math.Clamp(output[i], rgb.Lo, rgb.Hi));
        }
        return output;
    }
    private static double Sample(ReadOnlySpan<float> source, int width, int height, int x, int y, int c) =>
        x < 0 || x >= width || y < 0 || y >= height ? 1 : source[(y * width + x) * 4 + c];
}
