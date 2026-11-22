using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

internal static class PreviewRows
{
    // Below this a frame is converted on the calling thread. It was 4 MP until 2026-11-21, which left the frames
    // that matter most for responsiveness single-threaded: drag frames (about 1 MP, 18 ms) and screen-size
    // Auto frames (1.7-2.8 MP, 33-50 ms) whenever post-scan finishing is active.
    private const int ParallelPixelThreshold = 250_000;
    // Leave two hardware threads for Capture One; up to 16 workers (48 MP display conversion
    // went from 360 to 170 ms on 32 threads). Results do not depend on the worker count:
    // every row is computed independently.
    private static readonly int MaximumWorkers = Math.Clamp(Environment.ProcessorCount - 2, 1, 16);

    internal static void Process(int width, int height, CancellationToken token, Action<int, int> processRange)
    {
        token.ThrowIfCancellationRequested();
        var workers = Math.Min(Math.Min(Environment.ProcessorCount, MaximumWorkers), height);
        if ((long)width * height < ParallelPixelThreshold || workers <= 1)
        {
            processRange(0, height);
            return;
        }

        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers };
        try
        {
            Parallel.For(0, workers, options, worker =>
            {
                var start = (int)((long)height * worker / workers);
                var end = (int)((long)height * (worker + 1) / workers);
                processRange(start, end);
            });
        }
        catch (AggregateException error)
        {
            // Several workers can meet the same bad frame at once. Report it as the single-thread path does:
            // the first real error, or the cancellation when there is nothing else.
            var inner = error.Flatten().InnerExceptions;
            ExceptionDispatchInfo.Capture(inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner[0]).Throw();
        }
    }
}
