using System;
using System.Threading;

internal static class ImageScopesMath
{
    internal const int Bins = 256;
    internal const int DefaultMaximumSamples = 1024 * 1024;
    internal readonly record struct Sampling(int SourceWidth, int SourceHeight, int Width, int Height)
    {
        internal bool IsFull => SourceWidth == Width && SourceHeight == Height;
        internal int Count => checked(Width * Height);
        internal int SourceX(int x) => (int)((2L * x + 1) * SourceWidth / (2L * Width));
        internal int SourceY(int y) => (int)((2L * y + 1) * SourceHeight / (2L * Height));
    }
    internal sealed class Result(Sampling sampling)
    {
        internal Sampling Sampling { get; } = sampling;
        internal long VisiblePixels { get; set; }
        internal long TransparentPixels { get; set; }
        internal long BlackPixels { get; set; }
        internal long WhitePixels { get; set; }
        internal long AnyChannelLow { get; set; }
        internal long AnyChannelHigh { get; set; }
        internal long[] Low { get; } = new long[3];
        internal long[] High { get; } = new long[3];
        internal int[][] Histogram { get; } = CreatePlanes(4, Bins);
        internal int[][] Waveform { get; } = CreatePlanes(4, Bins * Bins);
        internal int[] Vectorscope { get; } = new int[Bins * Bins];
    }
    private static int[][] CreatePlanes(int count, int length)
    {
        var planes = new int[count][];
        for (int i = 0; i < count; ++i) planes[i] = new int[length];
        return planes;
    }
    // A centered regular grid is reproducible. The UI states when it samples;
    // sparse features can fall between cells and clipping then is an estimate.
    internal static Sampling Plan(int width, int height, int maximumSamples = DefaultMaximumSamples)
    {
        if (width <= 0 || height <= 0 || maximumSamples <= 0 || maximumSamples > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(maximumSamples), "Positive dimensions and a representable sample budget are required.");
        if ((long)width * height <= maximumSamples) return new(width, height, width, height);
        int columns = Math.Clamp((int)Math.Sqrt(maximumSamples * (double)width / height), 1, Math.Min(width, maximumSamples));
        int rows = Math.Min(height, maximumSamples / columns);
        return new(width, height, columns, rows);
    }
    // Input is tightly packed, straight-alpha BGRA8 on the grid returned by
    // Plan. Fully transparent pixels are excluded; partial alpha is unweighted.
    // RGB and Y' are encoded sRGB values, not scene-linear luminance or IRE.
    internal static Result Analyze(byte[] bgra, Sampling sampling, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (sampling.SourceWidth <= 0 || sampling.SourceHeight <= 0 || sampling.Width <= 0 || sampling.Height <= 0
            || sampling.Width > sampling.SourceWidth || sampling.Height > sampling.SourceHeight
            || (long)sampling.Width * sampling.Height * 4 != bgra.Length)
            throw new ArgumentException("BGRA samples do not match a valid sampling grid.", nameof(bgra));
        token.ThrowIfCancellationRequested();
        var result = new Result(sampling);
        var columns = new int[sampling.Width];
        for (int x = 0; x < columns.Length; ++x)
            columns[x] = sampling.SourceWidth == 1 ? 128 : Round(sampling.SourceX(x) * 255d / (sampling.SourceWidth - 1));
        for (int y = 0; y < sampling.Height; ++y)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < sampling.Width; ++x)
            {
                int pixel = (y * sampling.Width + x) * 4;
                if (bgra[pixel + 3] == 0) { ++result.TransparentPixels; continue; }
                ++result.VisiblePixels;
                int b = bgra[pixel], g = bgra[pixel + 1], r = bgra[pixel + 2];
                AddChannel(result, 0, r, columns[x]);
                AddChannel(result, 1, g, columns[x]);
                AddChannel(result, 2, b, columns[x]);
                int luma = LumaBin(r, g, b);
                ++result.Histogram[3][luma]; ++result.Waveform[3][luma * Bins + columns[x]];
                var vector = VectorBin(r, g, b);
                ++result.Vectorscope[vector.Y * Bins + vector.X];
                if (r == 0 && g == 0 && b == 0) ++result.BlackPixels;
                if (r == 255 && g == 255 && b == 255) ++result.WhitePixels;
                if (r == 0 || g == 0 || b == 0) ++result.AnyChannelLow;
                if (r == 255 || g == 255 || b == 255) ++result.AnyChannelHigh;
            }
        }
        return result;
    }
    private static void AddChannel(Result result, int channel, int code, int column)
    {
        ++result.Histogram[channel][code]; ++result.Waveform[channel][code * Bins + column];
        if (code == 0) ++result.Low[channel];
        if (code == 255) ++result.High[channel];
    }
    private static int Round(double value) => Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
    // Integer coefficients sum to 10000: all neutral codes produce exactly
    // zero chroma, including values at bin-rounding boundaries.
    internal static int LumaBin(int r, int g, int b) => (2126 * r + 7152 * g + 722 * b + 5000) / 10000;
    internal static (int X, int Y) VectorBin(int r, int g, int b)
    {
        int luma = 2126 * r + 7152 * g + 722 * b;
        return (Round(127.5 + (10000 * b - luma) / (2d * 9278)),
            Round(127.5 + (10000 * r - luma) / (2d * 7874)));
    }
}
