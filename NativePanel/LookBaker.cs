using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed record LookSettings(string Film, string Paper, double Exposure, double PrintExposure, double Strength,
    string Mode, string SpectralMethod, bool ScanInvert, Dictionary<string, double> Development,
    string FilmPushPullMode = "standard");
internal sealed record Stock(string Id, string Label, string? Kind = null, string? ChannelModel = null);
internal sealed record LookOptions(Stock[] Films, Stock[] Papers, Stock[] Modes, Stock[] SpectralMethods);

internal sealed class LookBaker : IDisposable
{
    private readonly string root;
    private readonly SemaphoreSlim slot = new(1);
    private readonly object sync = new();
    private Process? worker;
    private int sequence;
    private bool disposed;

    internal LookBaker()
    {
        root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")
            ?? throw new InvalidOperationException("Start Capture One from the Spektrafilm launcher.");
    }

    internal static bool IsAvailable => Environment.GetEnvironmentVariable("SPEKTRAFILM_PYTHON") is { } python && File.Exists(python);

    internal async Task<string> ExportLutAsync(LookSettings settings, int resolution, CancellationToken stop)
    {
        var reply = await RequestAsync(id => SettingsRequest(id, settings, resolution), stop).ConfigureAwait(false);
        return reply.GetProperty("cube").GetString() ?? throw new InvalidDataException("The renderer returned an empty LUT.");
    }

    private static Dictionary<string, object> SettingsRequest(int id, LookSettings settings, int resolution)
    {
        var request = new Dictionary<string, object>
        {
            ["id"] = id,
            ["command"] = "export_lut",
            ["size"] = resolution,
            ["film"] = settings.Film,
            ["paper"] = settings.Paper,
            ["mode"] = settings.Mode,
            ["spectral_method"] = settings.SpectralMethod,
            ["scan_invert"] = settings.ScanInvert,
            ["film_push_pull_mode"] = settings.FilmPushPullMode,
            ["exposure"] = settings.Exposure,
            ["printer_exposure"] = settings.PrintExposure,
            ["strength"] = settings.Strength,
        };
        foreach (var (key, value) in settings.Development)
            request.Add(key, value);
        return request;
    }

    private async Task<JsonElement> RequestAsync<T>(Func<int, T> request, CancellationToken stop)
    {
        await slot.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            stop.ThrowIfCancellationRequested();
            var process = GetWorker();
            var id = ++sequence;
            var json = JsonSerializer.Serialize(request(id));

            string? line;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                try
                {
                    // Finish reading an in-flight reply even if a newer controller value
                    // supersedes it. That keeps the worker stream synchronized.
                    await process.StandardInput.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                    line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (line is null)
                        throw new IOException("The Spektrafilm renderer exited. Adjust a control to retry.");
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    StopWorker();
                    throw new TimeoutException("The Spektrafilm renderer timed out. Adjust a control to retry.");
                }
                catch (IOException)
                {
                    StopWorker();
                    throw;
                }
            }

            stop.ThrowIfCancellationRequested();
            using var response = JsonDocument.Parse(line);
            var result = response.RootElement;
            if (result.GetProperty("id").GetInt32() != id)
            {
                StopWorker();
                throw new InvalidDataException("The Spektrafilm renderer returned an out-of-order reply.");
            }
            if (!result.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(result.GetProperty("error").GetString());

            return result.Clone();
        }
        finally
        {
            slot.Release();
        }
    }

    private Process GetWorker()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is { HasExited: false })
                return worker;
            worker?.Dispose();
            worker = null;

            var python = Environment.GetEnvironmentVariable("SPEKTRAFILM_PYTHON")
                ?? throw new InvalidOperationException("Run Setup LUT Export.cmd to enable optional LUT export.");
            var command = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root,
            };
            command.ArgumentList.Add("-u");
            command.ArgumentList.Add(Path.Combine(root, "lut_worker.py"));
            WorkerEnvironment.Sanitize(command);
            command.Environment.Remove("PYTHONPATH");
            command.Environment["PYTHONNOUSERSITE"] = "1";
            command.Environment["PYTHONUTF8"] = "1";

            var started = Process.Start(command) ?? throw new IOException("Could not start the Spektrafilm renderer.");
            started.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is { Length: > 0 } error)
                    StartupHook.Log("Spektrafilm renderer: " + error);
            };
            started.BeginErrorReadLine();
            worker = started;
            return started;
        }
    }

    private void StopWorker()
    {
        lock (sync)
        {
            if (worker is null)
                return;
            try
            {
                if (!worker.HasExited)
                    worker.Kill();
            }
            catch (InvalidOperationException) { }
            finally
            {
                worker.Dispose();
                worker = null;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            StopWorker();
        }
    }
}
