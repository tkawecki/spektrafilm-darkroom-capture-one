using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static class Rgb16Preview
{
    private static readonly float[] NormalizedCode = BuildNormalizedCodes();

    // The caller owns the immutable Capture One bitmap until this synchronous conversion completes.
    internal static byte[] Convert(int width, int height, int stride, nint data, bool hasAlpha, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const int outputChannels = 4;
        var inputChannels = hasAlpha ? 4 : 3;
        var inputRowLength = checked(width * inputChannels);
        var outputRowLength = checked(width * outputChannels);
        var result = GC.AllocateUninitializedArray<byte>(checked(outputRowLength * height * sizeof(float)));
        var blocks = Math.Min(height, Math.Min(Environment.ProcessorCount, 8));

        Parallel.For(0, blocks, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = blocks }, block =>
        {
            var row = GC.AllocateUninitializedArray<short>(inputRowLength);
            var codes = MemoryMarshal.Cast<short, ushort>(row.AsSpan());
            var floats = MemoryMarshal.Cast<byte, float>(result.AsSpan());
            for (var y = block * height / blocks; y < (block + 1) * height / blocks; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(data + y * stride, row, 0, inputRowLength);
                var output = floats.Slice(y * outputRowLength, outputRowLength);
                for (int read = 0, write = 0; read < inputRowLength; read += inputChannels, write += outputChannels)
                {
                    output[write] = NormalizedCode[codes[read]];
                    output[write + 1] = NormalizedCode[codes[read + 1]];
                    output[write + 2] = NormalizedCode[codes[read + 2]];
                    output[write + 3] = hasAlpha ? NormalizedCode[codes[read + 3]] : 1;
                }
            }
        });
        return result;
    }

    private static float[] BuildNormalizedCodes()
    {
        var codes = new float[ushort.MaxValue + 1];
        for (var i = 0; i < codes.Length; i++)
            codes[i] = i / (float)ushort.MaxValue;
        return codes;
    }
}
