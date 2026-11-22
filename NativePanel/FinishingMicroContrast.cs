using System;
using System.Threading;
using System.Threading.Tasks;

// The Dyeworks 2.2.3 scan_finishing.py: last display-referred stage,
// luma-only detail with sigma=1.5 px, truncate=3 and half-sample reflection.
// Andrea Volpato / Celian Le Bolloch; GPL-3.0, see Finishing.cs provenance.
internal static class FinishingMicroContrast
{
    private static readonly double[] Kernel = BuildKernel();

    internal static void Apply(float[] rgba, int width, int height, double amount, CancellationToken token)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid finishing frame dimensions.");
        if (!double.IsFinite(amount) || amount is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        token.ThrowIfCancellationRequested();
        if (amount == 0) return;
        var count = checked(width * height);
        var detail = new double[count];
        var horizontal = new double[count];
        var parallel = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Min(16, Environment.ProcessorCount) };
        void Rows(Action<int> row)
        {
            if (count >= 262144) Parallel.For(0, height, parallel, row);
            else for (var y = 0; y < height; y++) { token.ThrowIfCancellationRequested(); row(y); }
        }
        Rows(y =>
        {
            for (var x = 0; x < width; x++)
            {
                var p = (y * width + x) * 4;
                detail[y * width + x] = .2126 * rgba[p] + .7152 * rgba[p + 1] + .0722 * rgba[p + 2];
            }
        });
        Rows(y =>
        {
            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                double value = 0;
                for (var k = -5; k <= 5; k++) value += Kernel[k + 5] * detail[offset + Reflect(x + k, width)];
                horizontal[offset + x] = value;
            }
        });
        Rows(y =>
        {
            for (var x = 0; x < width; x++)
            {
                double blur = 0;
                for (var k = -5; k <= 5; k++) blur += Kernel[k + 5] * horizontal[Reflect(y + k, height) * width + x];
                var i = y * width + x;
                var delta = amount * (detail[i] - blur);
                var p = i * 4;
                rgba[p] = (float)(rgba[p] + delta); rgba[p + 1] = (float)(rgba[p + 1] + delta); rgba[p + 2] = (float)(rgba[p + 2] + delta);
            }
        });
    }

    private static int Reflect(int i, int length)
    {
        var cycle = length * 2;
        i %= cycle;
        if (i < 0) i += cycle;
        return i < length ? i : cycle - i - 1;
    }

    private static double[] BuildKernel()
    {
        var result = new double[11]; double sum = 0;
        for (var i = -5; i <= 5; i++) { result[i + 5] = Math.Exp(-.5 * i * i / (1.5 * 1.5)); sum += result[i + 5]; }
        for (var i = 0; i < result.Length; i++) result[i] /= sum;
        return result;
    }
}
