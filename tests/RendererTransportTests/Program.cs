using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

// Uses the production transport and real Vulkan worker, without Capture One APIs.
try
{
if (args[0] == "copy")
{
    using var fixtureDocument = JsonDocument.Parse(File.ReadAllText(args[1]));
    var bytes = File.ReadAllBytes(fixtureDocument.RootElement.GetProperty("result").GetProperty("path").GetString()!);
    using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(null, bytes.Length);
    using var accessor = mapping.CreateViewAccessor();
    using var stream = mapping.CreateViewStream(0, bytes.Length);
    var copies = new List<object>();
    for (var i = 0; i < 6; ++i)
    {
        var watch = Stopwatch.StartNew();
        accessor.WriteArray(0, bytes, 0, bytes.Length);
        var accessorMs = watch.Elapsed.TotalMilliseconds;
        stream.Position = 0;
        watch.Restart();
        stream.Write(bytes);
        var streamMs = watch.Elapsed.TotalMilliseconds;
        stream.Position = 0;
        watch.Restart();
        stream.Write(bytes, 0, bytes.Length);
        var directMs = watch.Elapsed.TotalMilliseconds;
        copies.Add(new { accessorMs, streamMs, directMs });
    }
    stream.Position = 0;
    if (!SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new Exception("Map copy changed bytes");
    File.WriteAllText(args[2], JsonSerializer.Serialize(copies, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS: exact mapped source bytes; " + args[2]);
    return;
}
if (args[0] is "fault" or "fault-input")
{
    if (args[0] == "fault") Environment.SetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_INPUT", "0");
    Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(args[1]));
    using var peer = new OpticalRenderer(nativePreview: true);
    var buffer = new byte[3000 * 3000 * 16];
    for (var attempt = 0; attempt < 2; ++attempt)
    {
        try
        {
            await peer.RenderAsync(3000, 3000, buffer, Array.Empty<string>(), CancellationToken.None, true);
            throw new Exception("Invalid acknowledgement left a mapping accepted on retry");
        }
        catch (InvalidDataException error)
        {
            if (args[0] == "fault-input" && !error.Message.Contains("shared source acknowledgement")) throw;
        }
    }
    Console.WriteLine("PASS: malformed MAP acknowledgements are rejected on both attempts; no stale mapping is accepted");
    return;
}
if (args[0] == "handshake")
{
    // A renderer from another build (for example one older than the handshake) is refused with a clear
    // message on every attempt, before any frame bytes are sent.
    Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(args[1]));
    using var mismatched = new OpticalRenderer(nativePreview: true);
    for (var attempt = 0; attempt < 2; ++attempt)
    {
        try
        {
            await mismatched.RenderAsync(1, 1, new byte[16], Array.Empty<string>(), CancellationToken.None, true);
            throw new Exception("A renderer from another build was accepted");
        }
        catch (InvalidDataException error) when (error.Message.Contains("does not match this panel")) { Console.WriteLine(error.Message); }
    }
    Console.WriteLine("PASS: a renderer without the matching handshake is refused on both attempts");
    return;
}
if (args[0] == "serial")
{
    // Overlapping requests on one worker must not interleave on its pipe: each result equals the same
    // request rendered alone. Then a disposed renderer refuses queued and new requests.
    Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(args[1]));
    const int w = 96, h = 64;
    byte[] Frame(float shift)
    {
        var values = new float[w * h * 4];
        for (var i = 0; i < w * h; ++i)
        {
            values[i * 4] = .05f + .8f * (i % w) / w + shift; values[i * 4 + 1] = .3f; values[i * 4 + 2] = .05f + .8f * (i / w) / h; values[i * 4 + 3] = 1;
        }
        var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes;
    }
    var requests = new (byte[] Frame, string[] RenderOptions)[] { (Frame(0), Array.Empty<string>()), (Frame(.05f), new[] { "--film-exposure", "1" }) };
    var alone = new List<string>();
    foreach (var (frame, renderOptions) in requests)
    {
        using var single = new OpticalRenderer(nativePreview: true);
        alone.Add(Convert.ToHexString(SHA256.HashData(await single.RenderAsync(w, h, frame, renderOptions, CancellationToken.None))));
    }
    if (alone[0] == alone[1]) throw new Exception("Test requests must differ");
    var shared = new OpticalRenderer(nativePreview: true);
    var overlapping = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
    {
        var (frame, renderOptions) = requests[i % 2];
        return (Index: i % 2, Hash: Convert.ToHexString(SHA256.HashData(await shared.RenderAsync(w, h, frame, renderOptions, CancellationToken.None))));
    })).ToArray();
    foreach (var result in await Task.WhenAll(overlapping))
        if (result.Hash != alone[result.Index]) throw new Exception("Overlapping requests changed pixels");
    var queued = Enumerable.Range(0, 4).Select(i => shared.RenderAsync(w, h, Frame(i), Array.Empty<string>(), CancellationToken.None)).ToArray();
    shared.Dispose();
    foreach (var task in queued)
    {
        try { await task; } // The request already running may finish; the queued ones are cancelled.
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
    }
    try
    {
        await shared.RenderAsync(w, h, requests[0].Frame, requests[0].RenderOptions, CancellationToken.None);
        throw new Exception("A disposed renderer accepted a request");
    }
    catch (ObjectDisposedException) { }
    Console.WriteLine("PASS: 12 overlapping requests equal single renders; a disposed renderer refuses queued and new requests");
    return;
}
Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(args[0]));
using var document = JsonDocument.Parse(File.ReadAllText(args[1]));
var fixture = document.RootElement.GetProperty("result");
var input = File.ReadAllBytes(fixture.GetProperty("path").GetString()!);
var width = fixture.GetProperty("width").GetInt32();
var height = fixture.GetProperty("height").GetInt32();
var options = fixture.GetProperty("options").EnumerateArray().Select(x => x.GetString()!).ToArray();
var exposure = Array.IndexOf(options, "--film-exposure") + 1;
if (exposure == 0) throw new Exception("Expected exposure option");
options[exposure] = "0";
var expected = File.ReadAllBytes(args[2]);
var expectedHash = Convert.ToHexString(SHA256.HashData(expected));
expected = Array.Empty<byte>();
var records = new List<object>();
using var renderer = new OpticalRenderer(nativePreview: true);
var replaceSource = args.Length > 4 && args[4] == "source-switch";
for (var i = 0; i < 5; i++)
{
    if (replaceSource) input = (byte[])input.Clone();
    options[exposure] = i % 2 == 0 ? "0" : "0.1";
    var clock = Stopwatch.StartNew();
    var pixels = await renderer.RenderAsync(width, height, input, options, CancellationToken.None, displayPixels: true);
    var ms = clock.Elapsed.TotalMilliseconds;
    var hash = Convert.ToHexString(SHA256.HashData(pixels));
    if (i % 2 == 0 && hash != expectedHash) throw new Exception("Native BGRA pixels changed");
    records.Add(new { kind = i == 0 ? "cold" : "warm", ms, hash });
}
if (args.Length > 4 && args[4] == "lifecycle")
{
    // Increasing output size reattaches the mapping without changing source identity.
    var floats = await renderer.RenderAsync(width, height, input, options, CancellationToken.None);
    Environment.SetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_OUTPUT", "0");
    Environment.SetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_INPUT", "0");
    using (var pipe = new OpticalRenderer(nativePreview: true))
    {
        var piped = await pipe.RenderAsync(width, height, input, options, CancellationToken.None);
        if (!floats.AsSpan().SequenceEqual(piped)) throw new Exception("Shared float precision changed");
    }
    Environment.SetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_OUTPUT", null);
    Environment.SetEnvironmentVariable("SPEKTRAFILM_NATIVE_SHARED_INPUT", null);
    floats = Array.Empty<byte>();
    var packedAgain = await renderer.RenderAsync(width, height, input, options, CancellationToken.None, true);
    if (Convert.ToHexString(SHA256.HashData(packedAgain)) != expectedHash) throw new Exception("BGRA after float resize changed");
    var small = new float[] { .2f, .4f, .6f, .5f };
    var smallBytes = new byte[16]; Buffer.BlockCopy(small, 0, smallBytes, 0, 16);
    var smaller = await renderer.RenderAsync(1, 1, smallBytes, options, CancellationToken.None, true);
    if (smaller.Length != 4 || smaller[3] != 128) throw new Exception("Smaller frame/alpha failed");
    options[exposure] = "0.1";
    using (var stop = new CancellationTokenSource(15))
    {
        try { await renderer.RenderAsync(width, height, input, options, stop.Token, true); throw new Exception("Render was not cancelled"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    options[exposure] = "0";
    var recovered = await renderer.RenderAsync(width, height, input, options, CancellationToken.None, true);
    if (Convert.ToHexString(SHA256.HashData(recovered)) != expectedHash) throw new Exception("Cancellation recovery changed pixels");
    Console.WriteLine("PASS: float parity, map growth/shrink, alpha, cancellation and worker recovery");
}
File.WriteAllText(args[3], JsonSerializer.Serialize(new { width, height, records }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS: real managed transport, exact native pixels; " + args[3]);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

internal static class StartupHook
{
    internal static void Log(string message) => Console.Error.WriteLine(message);
}
