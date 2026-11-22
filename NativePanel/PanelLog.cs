using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// The panel log, written on a background thread and kept to a bounded size: when it passes MaxBytes it is moved to
// "<name>.1" (replacing the previous one) and a new log starts. Before this the log grew without limit and every
// line was written on Capture One's UI thread. After upstream cd39774 (Spektrafilm for Capture One,
// Luka Kauzlaric, GPL-3.0).
internal static class PanelLog
{
    internal const long MaxBytes = 16L * 1024 * 1024;
    private static readonly Channel<(string Path, string Line)> Lines =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly object Gate = new();
    private static Task? writer;

    internal static void Write(string path, string line)
    {
        EnsureWriter();
        Lines.Writer.TryWrite((path, line));
    }

    private static void EnsureWriter()
    {
        if (writer is not null) return;
        lock (Gate)
        {
            if (writer is not null) return;
            writer = Task.Run(RunAsync);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(TimeSpan.FromSeconds(1));
        }
    }

    // Waits for the lines written so far (at most the timeout); used when Capture One exits.
    internal static void Flush(TimeSpan timeout)
    {
        Lines.Writer.TryComplete();
        try { writer?.Wait(timeout); }
        catch (AggregateException) { }
    }

    private static async Task RunAsync()
    {
        var batch = new StringBuilder();
        while (await Lines.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            string? path = null;
            while (Lines.Reader.TryRead(out var item))
            {
                if (path is not null && !string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    Append(path, batch);
                    batch.Clear();
                }
                path = item.Path;
                batch.Append(item.Line);
            }
            if (path is not null)
                Append(path, batch);
            batch.Clear();
        }
    }

    // Never stops the writer: a log that cannot be moved aside or written loses this batch only.
    private static void Append(string path, StringBuilder text)
    {
        try { Rotate(path); }
        catch (Exception) { }
        try { File.AppendAllText(path, text.ToString()); }
        catch (Exception) { }
    }

    internal static void Rotate(string path, long maxBytes = MaxBytes)
    {
        var log = new FileInfo(path);
        if (log.Exists && log.Length > maxBytes)
            File.Move(path, path + ".1", overwrite: true);
    }
}
