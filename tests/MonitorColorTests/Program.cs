using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Offscreen only: no Window, Application.Run, screenshots or OS configuration writes.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string colorDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool/drivers/color");
        byte[] patches = new byte[256 * 256 * 4];
        for (int i = 0; i < 256 * 256; ++i)
        {
            patches[i * 4] = (byte)((i % 32) * 255 / 31);
            patches[i * 4 + 1] = (byte)((i / 32 % 32) * 255 / 31);
            patches[i * 4 + 2] = (byte)((i / 1024 % 32) * 255 / 31);
            patches[i * 4 + 3] = 255;
        }
        var source = Make(patches, 256, 256);
        byte[] exportBefore = Png(source);
        var direct = Render(source);
        var cache = new MonitorColorCache();
        var originalDrawing = PreviewDrawing.Create(source);
        double cacheHitMilliseconds = 0;
        Require(patches.SequenceEqual(direct), "Actual PreviewDrawing preserves opaque BGRA samples exactly offscreen");
        var results = new List<object>();
        foreach (string profile in Directory.GetFiles(colorDir).Where(p => p.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Path.GetFileName(profile).StartsWith("CX271", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(profile).StartsWith("sRGB Color", StringComparison.OrdinalIgnoreCase))
                continue;
            var converted = new ColorConvertedBitmap(source, SrgbProfile.Context(),
                new ColorContext(new Uri(profile, UriKind.Absolute)), PixelFormats.Bgra32);
            converted.Freeze();
            var data = Read(converted);
            var drawn = Render(converted);
            var fi = new FileInfo(profile);
            var monitor = new MonitorColorProfile(new IntPtr(1), "fixture display", profile, fi.LastWriteTimeUtc.Ticks,
                fi.Length, Path.GetFileName(profile).StartsWith("sRGB Color"), false, null);
            var managed = cache.Prepare(source, originalDrawing, monitor);
            // An sRGB monitor needs no conversion: the panel paints the source itself. WIC's own sRGB-to-sRGB
            // transform is not bit-exact (it rounds), so it is only the reference for other profiles.
            var expected = monitor.IsSrgb ? patches : data;
            Require(expected.SequenceEqual(Read(managed.PaintBitmap)), "Assigned monitor profile must give the expected Viewer paint pixels: " + Path.GetFileName(profile));
            Require(ReferenceEquals(managed, cache.Prepare(source, originalDrawing, monitor)), "Repeated paints must reuse the cached result");
            var cacheClock = Stopwatch.StartNew();
            for (int hit = 0; hit < 10000; ++hit)
                cache.Prepare(source, originalDrawing, monitor);
            cacheClock.Stop();
            cacheHitMilliseconds = Math.Max(cacheHitMilliseconds, cacheClock.Elapsed.TotalMilliseconds / 10000);
            Require(patches.SequenceEqual(Read(source)), "Source/export pixels must remain unchanged");
            if (monitor.IsSrgb)
            {
                Require(ReferenceEquals(source, managed.PaintBitmap), "sRGB identity must not copy pixels");
                Require(ReferenceEquals(originalDrawing, managed.Drawing), "sRGB identity must reuse the original drawing");
            }
            var changedStamp = cache.Prepare(source, originalDrawing, monitor with { ModifiedTicks = monitor.ModifiedTicks + 1 });
            Require(!ReferenceEquals(managed, changedStamp), "Profile modification must invalidate conversion");
            var anotherMonitor = cache.Prepare(source, originalDrawing, monitor with { Monitor = new IntPtr(2), Device = "second fixture display" });
            Require(!ReferenceEquals(managed, anotherMonitor), "Monitor switch must select a separate cache key");
            var nextSource = Make(patches, 256, 256);
            var nextFrame = cache.Prepare(nextSource, PreviewDrawing.Create(nextSource), monitor);
            Require(!ReferenceEquals(nextFrame, managed) && ReferenceEquals(nextSource, nextFrame.Source), "New source frame must not reuse a previous bitmap's conversion");
            Require(Read(nextFrame.PaintBitmap).SequenceEqual(expected), "New source frame must receive the same correct ICC transform");
            Require(data.SequenceEqual(drawn), "PreviewDrawing preserves explicit ICC result without another offscreen conversion");
            int different = 0, maximum = 0;
            long sum = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int delta = Math.Abs((int)data[i] - patches[i]);
                maximum = Math.Max(maximum, delta);
                sum += delta;
                if (delta != 0) ++different;
            }
            if (!monitor.IsSrgb)
            {
                // Frames are converted in parallel bands on a worker thread before they are shown. Odd dimensions,
                // every pixel different: the bytes must equal one WIC pass over the whole frame.
                const int bandedWidth = 1601, bandedHeight = 1037;
                byte[] noise = new byte[bandedWidth * bandedHeight * 4];
                new Random(20261121).NextBytes(noise);
                for (int i = 3; i < noise.Length; i += 4) noise[i] = 255;
                var banded = Make(noise, bandedWidth, bandedHeight);
                var wholePass = Read(new ColorConvertedBitmap(banded, SrgbProfile.Context(),
                    new ColorContext(new Uri(profile, UriKind.Absolute)), PixelFormats.Bgra32));
                var aheadCache = new MonitorColorCache();
                var shown = aheadCache.Prepare(source, originalDrawing, monitor);
                Task.Run(() => aheadCache.PrepareAhead(banded, monitor)).Wait();
                var bandedDrawing = PreviewDrawing.Create(banded);
                var fromAhead = aheadCache.Prepare(banded, bandedDrawing, monitor);
                Require(wholePass.SequenceEqual(Read(fromAhead.PaintBitmap)), "Banded conversion must equal one WIC pass: " + Path.GetFileName(profile));
                Require(!ReferenceEquals(fromAhead.Drawing, bandedDrawing), "A frame prepared ahead must be the one painted");
                Require(ReferenceEquals(fromAhead, aheadCache.Prepare(banded, bandedDrawing, monitor)), "A painted ahead frame stays cached");
                Require(wholePass.SequenceEqual(Read(new MonitorColorCache().Prepare(banded, bandedDrawing, monitor).PaintBitmap)),
                    "Conversion in the Paint handler must give the same bytes as the one prepared ahead");
                // Frames prepared but never painted must not push the frame on screen out of the cache.
                for (int unused = 0; unused < 4; ++unused)
                {
                    var never = Make(patches, 256, 256);
                    Task.Run(() => aheadCache.PrepareAhead(never, monitor)).Wait();
                }
                Require(ReferenceEquals(shown, aheadCache.Prepare(source, originalDrawing, monitor)), "Unpainted ahead frames must not evict painted frames");
                Require(noise.SequenceEqual(Read(banded)), "Preparing ahead must not change the source frame");
                // Frames with transparency do not use the colour table; they must still equal WIC.
                byte[] translucent = (byte[])noise.Clone();
                translucent[bandedWidth * 4 * 500 + 3] = 128;
                var veiled = Make(translucent, bandedWidth, bandedHeight);
                Require(Read(new ColorConvertedBitmap(veiled, SrgbProfile.Context(), new ColorContext(new Uri(profile, UriKind.Absolute)), PixelFormats.Bgra32))
                    .SequenceEqual(Read(new MonitorColorCache().Prepare(veiled, PreviewDrawing.Create(veiled), monitor).PaintBitmap)),
                    "A frame with transparency must take the WIC pass");
                if (Path.GetFileName(profile).Contains("04sRGB", StringComparison.OrdinalIgnoreCase))
                {
                    // A 50 MP frame as the Viewer shows it at native resolution: smooth image content plus fine noise.
                    const int fullWidth = 8688, fullHeight = 5792;
                    byte[] full = new byte[fullWidth * fullHeight * 4];
                    var random = new Random(7);
                    for (int y = 0, i = 0; y < fullHeight; ++y)
                        for (int x = 0; x < fullWidth; ++x, i += 4)
                        {
                            int n = random.Next(-6, 7);
                            full[i] = (byte)Math.Clamp(x * 255 / fullWidth + n, 0, 255);
                            full[i + 1] = (byte)Math.Clamp(y * 255 / fullHeight + n, 0, 255);
                            full[i + 2] = (byte)Math.Clamp((x + y) * 255 / (fullWidth + fullHeight) - n, 0, 255);
                            full[i + 3] = 255;
                        }
                    var fullSource = Make(full, fullWidth, fullHeight);
                    var fullDrawing = PreviewDrawing.Create(fullSource);
                    var wicClock = Stopwatch.StartNew();
                    var wicFull = Read(new ColorConvertedBitmap(fullSource, SrgbProfile.Context(), new ColorContext(new Uri(profile, UriKind.Absolute)), PixelFormats.Bgra32));
                    wicClock.Stop();
                    var tableClock = Stopwatch.StartNew();
                    var tableFull = new MonitorColorCache().Prepare(fullSource, fullDrawing, monitor);
                    tableClock.Stop();
                    Require(wicFull.SequenceEqual(Read(tableFull.PaintBitmap)), "50 MP frame through the colour table must equal WIC");
                    Console.Error.WriteLine($"50 MP monitor conversion: WIC {wicClock.Elapsed.TotalMilliseconds:F0} ms, colour table {tableClock.Elapsed.TotalMilliseconds:F0} ms (table already built)");
                }
            }
            else
            {
                var identityCache = new MonitorColorCache();
                identityCache.PrepareAhead(source, monitor);
                Require(ReferenceEquals(originalDrawing, identityCache.Prepare(source, originalDrawing, monitor).Drawing), "sRGB needs no conversion ahead");
            }
            byte[] large = new byte[1600 * 1000 * 4];
            for (int i = 0; i < large.Length; ++i) large[i] = patches[i % patches.Length];
            var largeSource = Make(large, 1600, 1000);
            var timing = new List<double>();
            for (int attempt = 0; attempt < 4; ++attempt)
            {
                var clock = Stopwatch.StartNew();
                var transform = new ColorConvertedBitmap(largeSource, SrgbProfile.Context(),
                    new ColorContext(new Uri(profile, UriKind.Absolute)), PixelFormats.Bgra32);
                var transformed = Read(transform);
                clock.Stop();
                if (attempt > 0) timing.Add(clock.Elapsed.TotalMilliseconds);
                Require(transformed.Length == large.Length, "Full preview produced");
            }
            timing.Sort();
            results.Add(new {profile, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile))),
                maximumCodeDifference = maximum, meanRgbCodeDifference = sum / (256.0 * 256 * 3),
                changedChannels = different, fullPreviewMedianMilliseconds = timing[1],
                directHash = Convert.ToHexString(SHA256.HashData(direct)),
                convertedHash = Convert.ToHexString(SHA256.HashData(data)),
                convertedDrawingExact = data.SequenceEqual(drawn)});
        }
        var invalid = cache.Prepare(source, originalDrawing,
            new MonitorColorProfile(new IntPtr(1), "fixture", Path.Combine(colorDir, "missing-profile.icc"), 0, 0, false, false, null));
        Require(ReferenceEquals(source, invalid.PaintBitmap) && invalid.Warning is not null, "Missing ICC must preserve source and report a warning");
        var advanced = cache.Prepare(source, originalDrawing,
            new MonitorColorProfile(new IntPtr(1), "fixture", Path.Combine(colorDir, "missing-profile.icc"), 0, 0, false, true, "Advanced Color bypass"));
        Require(ReferenceEquals(source, advanced.PaintBitmap), "Advanced Color must bypass explicit monitor conversion");
        using var sourceProfile = new ColorContext(PixelFormats.Bgra32).OpenProfileStream();
        using var sourceBytes = new MemoryStream();
        sourceProfile.CopyTo(sourceBytes);
        // WPF's Bgra32 context follows the system's standard-sRGB setting, which colour tools can redirect (here once
        // to an Eizo profile). The panel uses Windows' sRGB file instead (SrgbProfile); record whether they differ.
        bool systemSrgbMatchesWpf = sourceBytes.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(colorDir, "sRGB Color Space Profile.icm")));
        Require(exportBefore.SequenceEqual(Png(source)), "Original sRGB PNG encoding remains byte-exact after paint conversions");
        IntPtr realMonitor = MonitorFromPoint(new NativePoint(), 2);
        var queryClock = Stopwatch.StartNew();
        var actual = MonitorColorNative.Read(realMonitor);
        queryClock.Stop();
        Require(actual.Warning is null || actual.AdvancedColor, "Actual Windows monitor profile query must succeed");
        var actualFrame = cache.Prepare(source, originalDrawing, actual);
        if (actual.IsSrgb || actual.AdvancedColor)
            Require(ReferenceEquals(source, actualFrame.PaintBitmap), "sRGB/Advanced Color policy uses exact no-copy source");
        else
            Require(Read(actualFrame.PaintBitmap).SequenceEqual(Read(new ColorConvertedBitmap(source,
                SrgbProfile.Context(), new ColorContext(new Uri(actual.Path!)), PixelFormats.Bgra32))),
                "Currently assigned SDR profile must match WIC");
        string report = JsonSerializer.Serialize(new { passed = true, offscreen = true,
            limitation = "Offscreen pixels alone do not prove desktop compositor behavior. Combine with Windows Advanced Color state and host/WPF code evidence.",
            actualPreviewDrawingRawExact = patches.SequenceEqual(direct), originalPngByteExact = exportBefore.SequenceEqual(Png(source)),
            maximumCacheHitMilliseconds = cacheHitMilliseconds, systemSrgbMatchesWpf,
            actualMonitor = new { actual.Device, actual.Path, actual.IsSrgb, actual.AdvancedColor, actual.Warning, queryMilliseconds = queryClock.Elapsed.TotalMilliseconds },
            profiles = results}, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length > 0 && !args[0].StartsWith("--")) File.WriteAllText(args[0], report);
        Console.WriteLine(report);
    }
    private static BitmapSource Make(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
    private static byte[] Read(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    private static byte[] Render(BitmapSource bitmap)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawDrawing(PreviewDrawing.Create(bitmap));
        var render = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        return Read(render);
    }
    private static void Require(bool ok, string text)
    {
        if (!ok) throw new InvalidOperationException(text);
    }
}
