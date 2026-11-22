using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class OpticalRenderer(bool export = false, bool nativePreview = false) : IDisposable
{
    private Process? process;
    private WeakReference<byte[]>? source;
    private MemoryMappedFile? outputMapping;
    private MemoryMappedViewAccessor? mappedOutput;
    private int mappedCapacity;
    private bool sharedOutputUnavailable = Environment.GetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_OUTPUT") == "0";
    private MemoryMappedFile? inputMapping;
    private MemoryMappedViewAccessor? mappedInput;
    private int inputCapacity;
    private bool sharedInputUnavailable = Environment.GetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_INPUT") == "0";

    // Request serialization and the HELLO handshake are adapted from nothing-complex/spektrafilm-for-capture-one
    // 285f0aa (GPL-3.0). One worker reads one command at a time from one pipe: overlapping requests (a
    // warm-up and a frame, or two callers of one export worker) would interleave their bytes.
    internal const int StreamProtocol = 2; // Must match kStreamProtocol in ofx-still-renderer/main.cpp.
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private bool disposed;
    private bool negotiated;

    internal async Task<byte[]> RenderAsync(int width, int height, byte[] rgba, string[] arguments, CancellationToken stop,
        bool displayPixels = false)
    {
        if (displayPixels && !nativePreview)
            throw new InvalidOperationException("Packed display pixels require the native Viewer renderer.");
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        await requests.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            // Disposed while this request waited: the caller already moved on, so this is a cancellation.
            if (disposed)
                throw new OperationCanceledException("The renderer was released.", lifetime.Token);
            return await RenderCoreAsync(width, height, rgba, arguments, linked.Token, displayPixels).ConfigureAwait(false);
        }
        finally { requests.Release(); }
    }

    private async Task<byte[]> RenderCoreAsync(int width, int height, byte[] rgba, string[] arguments, CancellationToken stop,
        bool displayPixels)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(export || nativePreview ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        try
        {
            var startup = PreviewPerformance.Start();
            var renderer = await GetReadyProcessAsync(token).ConfigureAwait(false);
            await PrepareSharedInputAsync(renderer, rgba.Length, token).ConfigureAwait(false);
            renderer = await GetReadyProcessAsync(token).ConfigureAwait(false); // A failed mapping restarts the worker.
            var expectedSize = displayPixels ? rgba.Length / sizeof(float) : rgba.Length;
            await PrepareSharedOutputAsync(renderer, expectedSize, token).ConfigureAwait(false);
            renderer = await GetReadyProcessAsync(token).ConfigureAwait(false);
            var shared = mappedOutput;
            PreviewPerformance.End(startup, "renderer.start", $"export={export} shared={shared is not null}");
            var input = renderer.StandardInput.BaseStream;
            var output = renderer.StandardOutput.BaseStream;
            var newSource = source is null || !source.TryGetTarget(out var previous) || !ReferenceEquals(previous, rgba);
            var verb = newSource ? (displayPixels ? "FRAME8" : "FRAME") : (displayPixels ? "REUSE8" : "REUSE");
            var sharedSource = newSource ? mappedInput : null;
            if (sharedSource is not null) verb = displayPixels ? "MFRAME8" : "MFRAME";
            var command = newSource ? FormattableString.Invariant($"{verb} {width} {height}") : verb;
            var header = Encoding.ASCII.GetBytes(command + " " + string.Join(' ', arguments) + "\n");
            var reply = new byte[8];
            var sent = PreviewPerformance.Start();
            if (sharedSource is not null)
            {
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    CopyToSharedFrame(sharedSource, rgba, token);
                    token.ThrowIfCancellationRequested();
                }, token).ConfigureAwait(false);
            }
            await input.WriteAsync(header, token).ConfigureAwait(false);
            if (newSource && sharedSource is null)
                await input.WriteAsync(rgba, token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
            PreviewPerformance.End(sent, "renderer.send", $"{width}x{height} {verb} export={export}");
            var ready = PreviewPerformance.Start();
            await output.ReadExactlyAsync(reply, token).ConfigureAwait(false);
            PreviewPerformance.End(ready, "renderer.wait", $"{width}x{height} {verb} export={export}");
            var code = BinaryPrimitives.ReadInt32LittleEndian(reply);
            var size = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
            if (code == 1 && size is >= 1 and <= 16384)
            {
                var error = new byte[size];
                await output.ReadExactlyAsync(error, token).ConfigureAwait(false);
                source = null;
                throw new InvalidOperationException(Encoding.UTF8.GetString(error));
            }
            if (code != 0 || size != expectedSize)
                throw new InvalidDataException("The optical renderer returned an invalid frame.");

            var receive = PreviewPerformance.Start();
            var pixels = GC.AllocateUninitializedArray<byte>(size);
            if (shared is not null)
            {
                token.ThrowIfCancellationRequested();
                CopySharedFrame(shared, pixels, size, token);
                token.ThrowIfCancellationRequested();
            }
            else
                await output.ReadExactlyAsync(pixels, token).ConfigureAwait(false);
            PreviewPerformance.End(receive, "renderer.receive", $"{width}x{height} bytes={size} export={export} shared={shared is not null}");
            if (newSource)
                source = new WeakReference<byte[]>(rgba);
            return pixels;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Stop(abort: true);
            var target = export ? "export" : nativePreview ? "native Viewer preview" : "preview";
            throw new TimeoutException($"Spektrafilm did not finish the {target} in time.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            Stop(abort: true);
            throw;
        }
    }

    private async Task PrepareSharedInputAsync(Process renderer, int size, CancellationToken token)
    {
        if (!nativePreview || export || sharedInputUnavailable || (mappedInput is null && size < 32 * 1024 * 1024))
            return;
        if (mappedInput is not null && inputCapacity >= size) return;
        ReleaseInputMapping();
        var name = "Local\\SpektrafilmSource-" + Guid.NewGuid().ToString("N");
        try
        {
            inputMapping = MemoryMappedFile.CreateNew(name, size, MemoryMappedFileAccess.ReadWrite);
            mappedInput = inputMapping.CreateViewAccessor(0, size, MemoryMappedFileAccess.Write);
            inputCapacity = size;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or OutOfMemoryException)
        {
            sharedInputUnavailable = true;
            Stop(abort: true);
            StartupHook.Log("Shared source unavailable; using pipes: " + error.Message);
            return;
        }
        var input = renderer.StandardInput.BaseStream;
        var output = renderer.StandardOutput.BaseStream;
        var command = Encoding.ASCII.GetBytes(FormattableString.Invariant($"SOURCE {name} {size}\n"));
        await input.WriteAsync(command, token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
        var reply = new byte[8];
        await output.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        var code = BinaryPrimitives.ReadInt32LittleEndian(reply);
        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
        if (code == 0 && length == 0) return;
        if (code != 1 || length is < 1 or > 16384)
            throw new InvalidDataException("Invalid shared source acknowledgement.");
        var errorText = new byte[length];
        await output.ReadExactlyAsync(errorText, token).ConfigureAwait(false);
        sharedInputUnavailable = true;
        Stop(abort: true);
        StartupHook.Log("Renderer uses source pipes: " + Encoding.UTF8.GetString(errorText));
    }

    private void ReleaseInputMapping()
    {
        mappedInput?.Dispose();
        mappedInput = null;
        inputMapping?.Dispose();
        inputMapping = null;
        inputCapacity = 0;
    }

    private async Task PrepareSharedOutputAsync(Process renderer, int size, CancellationToken token)
    {
        if (!nativePreview || export || sharedOutputUnavailable || (mappedOutput is null && size < 32 * 1024 * 1024))
            return;
        if (mappedOutput is not null && mappedCapacity >= size)
            return;
        ReleaseMapping();
        var name = "Local\\SpektrafilmPreview-" + Guid.NewGuid().ToString("N");
        try
        {
            outputMapping = MemoryMappedFile.CreateNew(name, size, MemoryMappedFileAccess.ReadWrite);
            mappedOutput = outputMapping.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
            mappedCapacity = size;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or OutOfMemoryException)
        {
            sharedOutputUnavailable = true;
            Stop(abort: true);
            StartupHook.Log("Shared preview unavailable; using pipes: " + error.Message);
            return;
        }
        var input = renderer.StandardInput.BaseStream;
        var output = renderer.StandardOutput.BaseStream;
        var command = Encoding.ASCII.GetBytes(FormattableString.Invariant($"MAP {name} {size}\n"));
        await input.WriteAsync(command, token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
        var reply = new byte[8];
        await output.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        var code = BinaryPrimitives.ReadInt32LittleEndian(reply);
        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
        if (code == 0 && length == 0) return;
        if (code != 1 || length is < 1 or > 16384)
            throw new InvalidDataException("Invalid shared preview acknowledgement.");
        var errorText = new byte[length];
        await output.ReadExactlyAsync(errorText, token).ConfigureAwait(false);
        sharedOutputUnavailable = true;
        Stop(abort: true);
        StartupHook.Log("Renderer uses preview pipes: " + Encoding.UTF8.GetString(errorText));
    }

    private void ReleaseMapping()
    {
        mappedOutput?.Dispose();
        mappedOutput = null;
        outputMapping?.Dispose();
        outputMapping = null;
        mappedCapacity = 0;
    }

    private async Task<Process> GetReadyProcessAsync(CancellationToken token)
    {
        var child = GetProcess();
        if (!negotiated)
        {
            await NegotiateAsync(child, token).ConfigureAwait(false);
            negotiated = true;
        }
        return child;
    }

    // A renderer from another build fails here with a clear message instead of misreading a later
    // command. Renderers older than the handshake answer HELLO with an unknown-command error.
    private static async Task NegotiateAsync(Process child, CancellationToken token)
    {
        var input = child.StandardInput.BaseStream;
        var output = child.StandardOutput.BaseStream;
        await input.WriteAsync(Encoding.ASCII.GetBytes(FormattableString.Invariant($"HELLO {StreamProtocol}\n")), token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
        var reply = new byte[8];
        await output.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        var code = BinaryPrimitives.ReadInt32LittleEndian(reply);
        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
        if (length is < 1 or > 16384 || code is not (0 or 1))
            throw new InvalidDataException(MismatchMessage("no valid handshake reply"));
        var text = new byte[length];
        await output.ReadExactlyAsync(text, token).ConfigureAwait(false);
        var message = Encoding.UTF8.GetString(text);
        if (code == 1)
            throw new InvalidDataException(MismatchMessage(message));
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("protocol", out var protocol)
                && protocol.TryGetInt32(out var version) && version == StreamProtocol)
                return;
        }
        catch (JsonException) { }
        throw new InvalidDataException(MismatchMessage(message));
    }

    private static string MismatchMessage(string detail) =>
        $"The Spektrafilm renderer does not match this panel (stream protocol {StreamProtocol}; renderer: {detail}). " +
        "Install the panel and renderer from the same build.";

    private Process GetProcess()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false })
                return process;
            Stop(abort: false);
            return process = StartWorker();
        }
    }

    private Process StartWorker()
    {
        var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")
            ?? throw new InvalidOperationException("Start Capture One using the Spektrafilm launcher.");
        var exe = Path.Combine(root, "renderer", "SpektraStillRenderer.exe");
        if (!File.Exists(exe))
            throw new IOException("The Spektrafilm Vulkan renderer is missing. See the project README.");
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        WorkerEnvironment.Sanitize(start);
        if (nativePreview && !export)
        {
            // Keep the native sampling/overlap, but amortize per-tile GPU work.
            // Do not change exports or override explicitly selected tile sizes.
            start.Environment.TryAdd("SPEKTRAFILM_VULKAN_TILE_WIDTH", "2048");
            start.Environment.TryAdd("SPEKTRAFILM_VULKAN_TILE_HEIGHT", "2048");
        }
        start.ArgumentList.Add(export ? "--export-stream" : nativePreview ? "--native-preview-stream" : "--stream");
        var spawned = PreviewPerformance.Start();
        var child = Process.Start(start) ?? throw new IOException("Could not start the optical renderer.");
        PreviewPerformance.End(spawned, "renderer.spawn", $"pid={child.Id} native={nativePreview} export={export}");
        child.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
                StartupHook.Log("Optical renderer: " + args.Data);
        };
        child.BeginErrorReadLine();
        return child;
    }

    // A disposed renderer refuses new requests and cancels queued ones; it never starts a worker again.
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }
        lifetime.Cancel();
        Stop(abort: false);
    }

    internal bool Started => process is { HasExited: false };

    private static void CopyToSharedFrame(MemoryMappedViewAccessor shared, byte[] rgba, CancellationToken token)
    {
        const int chunk = 16 * 1024 * 1024;
        var chunks = (rgba.Length + chunk - 1) / chunk;
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 8) };
        Parallel.For(0, chunks, options, index =>
        {
            var offset = (long)index * chunk;
            shared.WriteArray(offset, rgba, (int)offset, (int)Math.Min(chunk, rgba.Length - offset));
        });
    }

    // A native Viewer frame in float is up to 0.8 GB. A single ReadArray copied it at about
    // 1.3 GB/s (0.6 s); independent 16 MB chunks on several threads copy the same bytes faster.
    private static void CopySharedFrame(MemoryMappedViewAccessor shared, byte[] pixels, int size, CancellationToken token)
    {
        const int chunk = 16 * 1024 * 1024;
        if (size <= 4 * chunk)
        {
            if (shared.ReadArray(0, pixels, 0, size) != size)
                throw new InvalidDataException("The shared preview frame is incomplete.");
            return;
        }
        var chunks = (size + chunk - 1) / chunk;
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 8) };
        Parallel.For(0, chunks, options, index =>
        {
            var offset = (long)index * chunk;
            var count = (int)Math.Min(chunk, size - offset);
            if (shared.ReadArray(offset, pixels, (int)offset, count) != count)
                throw new InvalidDataException("The shared preview frame is incomplete.");
        });
    }

    // Starts the process, Vulkan, its pipelines and the stock's tables with a tiny frame, so the
    // first real frame does not pay for them (about 0.3 s per renderer). It never throws: a
    // failure is left to the real render, which reports it.
    internal async Task WarmUpAsync(string[] arguments)
    {
        if (Started)
            return;
        const int size = 16;
        var rgba = new float[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 0.18f;
            rgba[i + 3] = 1f;
        }
        var bytes = new byte[rgba.Length * sizeof(float)];
        Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        var started = PreviewPerformance.Start();
        try
        {
            await RenderAsync(size, size, bytes, arguments, CancellationToken.None).ConfigureAwait(false);
            PreviewPerformance.End(started, "renderer.warmup", $"native={nativePreview} export={export}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            PreviewPerformance.End(started, "renderer.warmup", "failed: " + error.GetBaseException().Message);
        }
    }

    private void Stop(bool abort)
    {
        Process? child;
        lock (sync)
        {
            child = process;
            process = null;
            source = null;
            negotiated = false;
            ReleaseMapping();
            ReleaseInputMapping();
        }
        if (child is null)
            return;
        if (abort)
            CloseWorker(child, abort: true);
        else
            _ = Task.Run(() => CloseWorker(child, abort: false));
    }

    private static void CloseWorker(Process child, bool abort)
    {
        try
        {
            if (child.HasExited)
                return;
            if (!abort)
            {
                child.StandardInput.Close();
                if (child.WaitForExit(300))
                    return;
            }
            if (!child.HasExited)
                child.Kill();
        }
        catch (IOException)
        {
            try { if (!child.HasExited) child.Kill(); }
            catch (InvalidOperationException) { }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception error) { StartupHook.Log($"Optical renderer shutdown: {error.Message}"); }
        finally
        {
            child.Dispose();
        }
    }
}
