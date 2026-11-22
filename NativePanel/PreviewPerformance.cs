using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

// Opt-in stage timings. No image data or settings are recorded here.
internal static class PreviewPerformance
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("SPEKTRAFILM_PERF_LOG");
    private static readonly object Sync = new();
    internal static long Start() => string.IsNullOrWhiteSpace(Path) ? 0 : Stopwatch.GetTimestamp();
    internal static void End(long started, string stage, string detail = "")
    {
        if (started == 0 || Path is null) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O}\t{stage}\t{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}\t{detail}{Environment.NewLine}");
        try { lock (Sync) File.AppendAllText(Path, line); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }
}
