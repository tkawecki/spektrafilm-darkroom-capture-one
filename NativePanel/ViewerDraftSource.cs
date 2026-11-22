using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static class ViewerDraftSource
{
    private const int MaxSide = 2560;
    private const int BytesPerPixel = 4 * sizeof(float);
    private const int RowsPerBlock = 8;

    internal static HostImageSource.Frame Downsample(HostImageSource.Frame frame, int maxLongEdge, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLongEdge);
        var sourcePixels = (long)frame.Width * frame.Height;
        if (frame.Width <= 0 || frame.Height <= 0 || sourcePixels != frame.FloatBytes.Length / BytesPerPixel
            || frame.FloatBytes.Length % BytesPerPixel != 0)
            throw new ArgumentException("Invalid draft preview source frame.", nameof(frame));

        token.ThrowIfCancellationRequested();
        // Limiting the long edge also caps square frames at 6,553,600 pixels.
        var scale = Math.Min(1, (double)Math.Min(maxLongEdge, MaxSide) / Math.Max(frame.Width, frame.Height));
        var width = Math.Max(1, (int)Math.Floor(frame.Width * scale + 1e-9));
        var height = Math.Max(1, (int)Math.Floor(frame.Height * scale + 1e-9));
        if (width == frame.Width && height == frame.Height)
            return frame;

        // Average the full source area covered by each destination pixel. Fractional
        // edges keep the filter centered and preserve the same normalized canvas.
        var columns = BuildBoxes(frame.Width, width);
        var rows = BuildBoxes(frame.Height, height);
        var normalizer = (float)((double)width * height / sourcePixels);
        var draft = new byte[checked(width * height * BytesPerPixel)];
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
        };
        Parallel.For(0, (height + RowsPerBlock - 1) / RowsPerBlock, options, block =>
        {
            var source = MemoryMarshal.Cast<byte, Vector4>(frame.FloatBytes.AsSpan());
            var destination = MemoryMarshal.Cast<byte, Vector4>(draft.AsSpan());
            var end = Math.Min(height, (block + 1) * RowsPerBlock);
            for (var y = block * RowsPerBlock; y < end; y++)
            {
                token.ThrowIfCancellationRequested();
                var targetRow = destination.Slice(y * width, width);
                var row = rows[y];
                for (var sourceY = row.First; sourceY <= row.Last; sourceY++)
                {
                    var rowWeight = sourceY == row.First ? row.FirstWeight : sourceY == row.Last ? row.LastWeight : 1;
                    var sourceRow = source.Slice(sourceY * frame.Width, frame.Width);
                    for (var x = 0; x < width; x++)
                    {
                        var column = columns[x];
                        var sum = sourceRow[column.First] * column.FirstWeight;
                        for (var sourceX = column.First + 1; sourceX < column.Last; sourceX++)
                            sum += sourceRow[sourceX];
                        if (column.Last != column.First)
                            sum += sourceRow[column.Last] * column.LastWeight;
                        targetRow[x] += sum * rowWeight;
                    }
                }
                for (var x = 0; x < width; x++)
                    targetRow[x] *= normalizer;
            }
        });
        System.Drawing.Rectangle? crop = null;
        if (frame.PreviewCrop is { } original)
        {
            int left = Math.Clamp((int)Math.Round(original.Left * (double)width / frame.Width), 0, width - 1);
            int top = Math.Clamp((int)Math.Round(original.Top * (double)height / frame.Height), 0, height - 1);
            crop = new(left, top,
                Math.Clamp((int)Math.Round(original.Width * (double)width / frame.Width), 1, width - left),
                Math.Clamp((int)Math.Round(original.Height * (double)height / frame.Height), 1, height - top));
        }
        return new HostImageSource.Frame(width, height, Array.Empty<byte>(), draft, frame.BitDepth)
        { PreviewCrop = crop, PreviewCropError = frame.PreviewCropError };
    }

    private static Box[] BuildBoxes(int sourceLength, int destinationLength)
    {
        var boxes = new Box[destinationLength];
        for (var i = 0; i < boxes.Length; i++)
        {
            // Work in units of 1 / destinationLength of a source pixel.
            var start = (long)i * sourceLength;
            var end = start + sourceLength;
            var first = (int)(start / destinationLength);
            var last = (int)((end - 1) / destinationLength);
            var firstWeight = (Math.Min((long)(first + 1) * destinationLength, end) - start) / (float)destinationLength;
            var lastWeight = (end - (long)last * destinationLength) / (float)destinationLength;
            boxes[i] = new Box(first, last, firstWeight, lastWeight);
        }
        return boxes;
    }

    private readonly record struct Box(int First, int Last, float FirstWeight, float LastWeight);
}
