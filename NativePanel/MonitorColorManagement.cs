using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Display correction belongs to the paint surface only. Never pass these pixels
// back to SpatialPreview's frame, PNG writer, LUT, or 16-bit export pipeline.
internal sealed class MonitorColorManagement : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Action invalidate;
    private readonly Action<string> log;
    private readonly DispatcherTimer timer;
    private readonly MonitorColorCache cache = new();
    private readonly Dictionary<IntPtr, MonitorColorProfile> profiles = new();
    private FrameworkElement? view;
    private IntPtr monitor;
    private bool reading;
    private volatile bool disposed;
    private string? logged;
    // The profile of the monitor the Viewer was last painted on; read by PrepareAhead on worker threads.
    private volatile MonitorColorProfile? paintedProfile;

    internal MonitorColorManagement(Dispatcher dispatcher, Action invalidate, Action<string> log)
    {
        this.dispatcher = dispatcher;
        this.invalidate = invalidate;
        this.log = log;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        timer.Stop();
    }

    internal string? Warning { get; private set; }

    internal DrawingGroup GetDrawing(FrameworkElement target, BitmapSource source, DrawingGroup original)
    {
        view = target;
        timer.Start();
        IntPtr selected = MonitorColorNative.MonitorFor(target);
        if (selected != monitor || !profiles.ContainsKey(selected))
        {
            monitor = selected;
            Refresh();
        }
        if (!profiles.TryGetValue(selected, out var profile))
        {
            Warning = "Reading the Windows monitor color profile; Viewer temporarily uses sRGB.";
            return original;
        }
        paintedProfile = profile;
        var prepared = cache.Prepare(source, original, profile);
        Warning = prepared.Warning;
        LogOnce($"Viewer monitor color: {profile.Device}; {profile.Description}; {prepared.Warning ?? "ready"}.");
        return prepared.Drawing;
    }

    // Converts a frame for the current monitor before it is shown, on the caller's (worker) thread, so the
    // Paint handler finds it ready. Without it the conversion of a 50 MP frame stalls the UI thread for ~0.5 s.
    // Safe to skip: Paint converts whatever was not prepared.
    internal void PrepareAhead(BitmapSource source, CancellationToken token)
    {
        if (!disposed && paintedProfile is { } profile)
            cache.PrepareAhead(source, profile, token);
    }

    private void Refresh()
    {
        if (disposed || reading || view is not { IsVisible: true }) return;
        IntPtr requested = MonitorColorNative.MonitorFor(view);
        monitor = requested;
        reading = true;
        // GetICMProfile/profile file reads never run in a Paint handler. In some
        // driver/WCS configurations a first query can take several seconds.
        _ = Task.Run(() =>
        {
            var read = MonitorColorNative.Read(requested);
            // The 64 MB colour table is built here, not in the first Paint. A failure is reported by Paint.
            try { MonitorColorCache.Warm(read); } catch (Exception) { }
            return read;
        }).ContinueWith(task =>
        {
            if (dispatcher.HasShutdownStarted) return;
            _ = dispatcher.InvokeAsync(() =>
            {
                reading = false;
                if (disposed) return;
                var result = task.IsCompletedSuccessfully ? task.Result
                    : MonitorColorProfile.Fallback(requested, "Monitor profile query failed; Viewer uses sRGB.");
                bool changed = !profiles.TryGetValue(requested, out var old) || result != old;
                profiles[requested] = result;
                if (monitor == requested) paintedProfile = result;
                if (profiles.Count > 8)
                    foreach (var key in profiles.Keys.Where(k => k != requested).ToArray()) profiles.Remove(key);
                if (changed)
                {
                    try { invalidate(); }
                    catch (Exception error) { LogOnce($"Viewer monitor color refresh failed: {error.Message}"); }
                }
                if (monitor != requested) Refresh();
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void LogOnce(string message)
    {
        if (logged == message) return;
        logged = message;
        log(message);
    }

    internal void Clear()
    {
        view = null;
        paintedProfile = null;
        timer.Stop();
        cache.Clear();
        Warning = null;
    }

    public void Dispose()
    {
        disposed = true;
        Clear();
        profiles.Clear();
    }
}

internal sealed record MonitorColorProfile(IntPtr Monitor, string Device, string? Path,
    long ModifiedTicks, long Length, bool IsSrgb, bool AdvancedColor, string? Warning)
{
    internal string Description => AdvancedColor ? "Windows Advanced Color (sRGB bypass)"
        : IsSrgb ? "sRGB identity" : Path ?? "sRGB fallback";
    internal static MonitorColorProfile Fallback(IntPtr monitor, string warning) =>
        new(monitor, "unknown display", null, 0, 0, true, false, warning);
}

internal sealed record MonitorColorFrame(BitmapSource Source, MonitorColorProfile Profile,
    BitmapSource PaintBitmap, DrawingGroup Drawing, string? Warning);

// A small per-Viewer cache: repeated paints/pans/zooms allocate no pixels. The
// original frozen bitmap is retained verbatim for identity and error paths.
internal sealed class MonitorColorCache
{
    private const int MinimumBandRows = 256;
    private readonly object gate = new();
    private readonly List<MonitorColorFrame> frames = new();
    // Frames converted before their first paint, newest last. They are kept apart from the painted frames, so
    // frames that were prepared but never shown cannot push the frame on screen out of the cache. Two entries:
    // a quick frame finishing next to the full-size frame must not drop it.
    private readonly List<MonitorColorFrame> ahead = new();
    private int generation;

    internal void Clear()
    {
        lock (gate)
        {
            generation++;
            frames.Clear();
            ahead.Clear();
        }
    }

    internal static void Warm(MonitorColorProfile profile)
    {
        if (NeedsConversion(profile)) MonitorColorTable.For(profile);
    }

    internal MonitorColorFrame Prepare(BitmapSource source, DrawingGroup original, MonitorColorProfile profile)
    {
        lock (gate)
        {
            foreach (var frame in frames)
                if (ReferenceEquals(frame.Source, source) && frame.Profile == profile) return frame;
            var ready = ahead.FindIndex(frame => ReferenceEquals(frame.Source, source) && frame.Profile == profile);
            if (ready >= 0)
            {
                var prepared = ahead[ready];
                ahead.RemoveAt(ready);
                return Keep(prepared);
            }
        }
        var converted = Convert(source, original, profile, CancellationToken.None)!;
        lock (gate) return Keep(converted);
    }

    internal void PrepareAhead(BitmapSource source, MonitorColorProfile profile, CancellationToken token = default)
    {
        if (!NeedsConversion(profile)) return;
        int started;
        lock (gate)
        {
            started = generation;
            foreach (var frame in frames.Concat(ahead))
                if (ReferenceEquals(frame.Source, source) && frame.Profile == profile) return;
        }
        // A failed conversion (null) is left to Paint, which reports it with the frame's own drawing.
        if (Convert(source, null, profile, token) is not { } prepared) return;
        lock (gate)
        {
            if (started != generation) return; // cleared meanwhile: do not keep a frame nobody will paint
            if (ahead.Count == 2) ahead.RemoveAt(0);
            ahead.Add(prepared);
        }
    }

    private MonitorColorFrame Keep(MonitorColorFrame prepared)
    {
        // Base frame, Viewer window (SpatialPreview's zoomed-in window) and the frame replacing one of them.
        if (frames.Count == 3) frames.RemoveAt(0);
        frames.Add(prepared);
        return prepared;
    }

    private static bool NeedsConversion(MonitorColorProfile profile) =>
        !profile.IsSrgb && !profile.AdvancedColor && profile.Path is not null;

    // original == null: called ahead of the paint; returns null when the conversion fails or is cancelled late.
    private static MonitorColorFrame? Convert(BitmapSource source, DrawingGroup? original, MonitorColorProfile profile, CancellationToken token)
    {
        if (!NeedsConversion(profile))
            return original is null ? null : new MonitorColorFrame(source, profile, source, original, profile.Warning);
        try
        {
            var painted = ConvertPixels(source, profile, token);
            return new MonitorColorFrame(source, profile, painted, PreviewDrawing.Create(painted), profile.Warning);
        }
        catch (OperationCanceledException) when (original is null) { throw; }
        catch (Exception error)
        {
            return original is null ? null
                : new MonitorColorFrame(source, profile, source, original, $"Monitor profile conversion failed; Viewer uses sRGB. {error.Message}");
        }
    }

    // Converts the frame now, once, rather than lazily during every WPF render. Opaque Bgra32 frames (all Viewer
    // frames) go through the profile's exact colour table in parallel bands; anything else takes one WIC pass.
    private static BitmapSource ConvertPixels(BitmapSource source, MonitorColorProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int width = source.PixelWidth, height = source.PixelHeight;
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bool mapped = source.Format == PixelFormats.Bgra32 && source.IsFrozen;
        if (mapped)
        {
            uint[] table = MonitorColorTable.For(profile);
            // One core stays free for the UI thread and the renderer's transport.
            int workers = Math.Max(1, Environment.ProcessorCount - 1);
            int bands = Math.Clamp(Math.Min(height / MinimumBandRows, workers), 1, 32);
            int translucent = 0;
            void ConvertBand(int band)
            {
                int top = (int)((long)height * band / bands), rows = (int)((long)height * (band + 1) / bands) - top;
                source.CopyPixels(new Int32Rect(0, top, width, rows), pixels, stride, checked(top * stride));
                var colours = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(top * stride, rows * stride));
                uint alpha = 0xFF000000;
                for (int i = 0; i < colours.Length; i++)
                {
                    uint colour = colours[i];
                    alpha &= colour;
                    colours[i] = table[colour & 0xFFFFFF];
                }
                if (alpha != 0xFF000000) Interlocked.Exchange(ref translucent, 1);
            }
            if (bands == 1) ConvertBand(0);
            else Parallel.For(0, bands, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers }, ConvertBand);
            mapped = translucent == 0;
        }
        if (!mapped)
        {
            var converted = new ColorConvertedBitmap(source, SrgbProfile.Context(),
                new ColorContext(new Uri(profile.Path!, UriKind.Absolute)), PixelFormats.Bgra32);
            converted.CopyPixels(pixels, stride, 0);
        }
        token.ThrowIfCancellationRequested();
        var painted = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        painted.Freeze();
        return painted;
    }
}

// The monitor transform of every opaque 8-bit sRGB colour, taken from WIC itself, so a table lookup gives the
// same bytes as a WIC pass over the frame. WIC converts a 50 MP frame in about 0.5 s on one thread and does not
// scale across threads; the lookup takes a fraction of that. One table is 64 MB and is built once per profile.
internal static class MonitorColorTable
{
    private const int Side = 4096, ChunkRows = 256;
    private static readonly object gate = new();
    private static readonly List<(string Path, long Ticks, long Length, uint[] Table)> tables = new();

    internal static uint[] For(MonitorColorProfile profile)
    {
        lock (gate)
        {
            foreach (var entry in tables)
                if (entry.Path == profile.Path && entry.Ticks == profile.ModifiedTicks && entry.Length == profile.Length) return entry.Table;
            var table = Build(profile.Path!);
            if (tables.Count == 2) tables.RemoveAt(0);
            tables.Add((profile.Path!, profile.ModifiedTicks, profile.Length, table));
            return table;
        }
    }

    private static uint[] Build(string profilePath)
    {
        var table = new uint[Side * Side];
        var chunk = new uint[Side * ChunkRows];
        var bytes = new byte[chunk.Length * 4];
        var target = new ColorContext(new Uri(profilePath, UriKind.Absolute));
        for (int top = 0; top < Side; top += ChunkRows)
        {
            for (int i = 0; i < chunk.Length; i++) chunk[i] = 0xFF000000 | (uint)(top * Side + i);
            MemoryMarshal.AsBytes(chunk.AsSpan()).CopyTo(bytes);
            var colours = BitmapSource.Create(Side, ChunkRows, 96, 96, PixelFormats.Bgra32, null, bytes, Side * 4);
            var converted = new ColorConvertedBitmap(colours, SrgbProfile.Context(), target, PixelFormats.Bgra32);
            converted.CopyPixels(bytes, Side * 4, 0);
            MemoryMarshal.Cast<byte, uint>(bytes).CopyTo(table.AsSpan(top * Side, chunk.Length));
        }
        return table;
    }
}

internal static class MonitorColorNative
{
    internal static IntPtr MonitorFor(FrameworkElement view)
    {
        try
        {
            Point a = view.PointToScreen(new Point(0, 0));
            Point b = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
            var rect = new NativeRect { Left = (int)Math.Floor(a.X), Top = (int)Math.Floor(a.Y),
                Right = (int)Math.Ceiling(b.X), Bottom = (int)Math.Ceiling(b.Y) };
            // Largest intersection with the Viewer, not the panel or main-window
            // origin. For a Viewer spanning displays there is one chosen profile.
            return MonitorFromRect(ref rect, 2);
        }
        catch { return IntPtr.Zero; }
    }

    internal static MonitorColorProfile Read(IntPtr monitor)
    {
        try
        {
            if (monitor == IntPtr.Zero) throw new InvalidOperationException("Viewer monitor is unavailable.");
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            bool advanced = AdvancedColorEnabled(info.Device);
            if (advanced)
                return new(monitor, info.Device, null, 0, 0, true, true,
                    "Windows Advanced Color is active; Viewer uses system-managed sRGB. Legacy ICC compatibility mode has not been validated.");
            IntPtr dc = CreateDC("DISPLAY", info.Device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            string profile;
            try
            {
                uint length = 32768;
                var buffer = new StringBuilder((int)length);
                if (!GetICMProfile(dc, ref length, buffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
                profile = System.IO.Path.GetFullPath(buffer.ToString());
            }
            finally { DeleteDC(dc); }
            var file = new FileInfo(profile);
            if (!file.Exists) throw new FileNotFoundException("Assigned monitor profile is missing.", profile);
            // Compare bytes with the sRGB source profile, never profile filenames
            // (an Eizo sRGB-mode calibration differs from sRGB).
            byte[] assigned = File.ReadAllBytes(profile);
            bool srgb = assigned.AsSpan().SequenceEqual(SrgbProfile.Bytes);
            return new(monitor, info.Device, profile, file.LastWriteTimeUtc.Ticks, file.Length, srgb, false, null);
        }
        catch (Exception error)
        {
            return MonitorColorProfile.Fallback(monitor,
                $"Windows monitor profile could not be read; Viewer uses sRGB. {error.Message}");
        }
    }

    private static bool AdvancedColorEnabled(string device)
    {
        // Paths and mode records use SDK layouts (72 and 64 bytes). Query only
        // active paths. INFO_2 (type 15) first; builds before Windows 11 24H2
        // answer it with an error and use INFO (type 9).
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            int error = GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount);
            if (error != 0) throw new Win32Exception(error);
            IntPtr paths = Marshal.AllocHGlobal(checked((int)pathCount * 72));
            IntPtr modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                error = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception(error);
                for (int i = 0; i < pathCount; ++i)
                {
                    IntPtr path = IntPtr.Add(paths, i * 72);
                    var source = new SourceName { Header = new DeviceHeader { Type = 1, Size = 84,
                        Low = Marshal.ReadInt32(path, 0), High = Marshal.ReadInt32(path, 4), Id = Marshal.ReadInt32(path, 8) }, Name = "" };
                    error = GetSourceName(ref source);
                    if (error != 0) throw new Win32Exception(error);
                    if (!string.Equals(source.Name, device, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = new DeviceHeader { Low = Marshal.ReadInt32(path, 20), High = Marshal.ReadInt32(path, 24), Id = Marshal.ReadInt32(path, 28) };
                    // Windows 11 24H2 and later report the active colour mode directly (INFO_2): 1 = automatic
                    // colour management (wide colour on an SDR display), 2 = HDR. In both, Windows maps the
                    // Viewer's sRGB itself, so a monitor profile must not be applied again. Older builds do
                    // not know this query and use INFO below. After flx-film-lab DisplayColour (GPL-3.0).
                    var modern = new AdvancedInfo2 { Header = target with { Type = 15, Size = 36 } };
                    if (GetAdvancedInfo2(ref modern) == 0)
                        return modern.ActiveColorMode != 0;
                    var color = new AdvancedInfo { Header = target with { Type = 9, Size = 32 } };
                    error = GetAdvancedInfo(ref color);
                    if (error != 0) throw new Win32Exception(error);
                    return (color.Flags & 2) != 0;
                }
                throw new InvalidOperationException("No active display path matches the Viewer monitor.");
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("Display configuration changed during the monitor query.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceHeader { internal int Type, Size, Low, High, Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        internal DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedInfo { internal DeviceHeader Header; internal uint Flags, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedInfo2 { internal DeviceHeader Header; internal uint Flags, Encoding, Bits, ActiveColorMode; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetICMProfileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetICMProfile(IntPtr dc, ref uint count, StringBuilder path);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedInfo(ref AdvancedInfo info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedInfo2(ref AdvancedInfo2 info);
}
