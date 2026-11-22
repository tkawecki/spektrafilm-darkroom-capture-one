using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static Assembly panel = null!;
    private static Type frameType = null!, spaceType = null!, previewType = null!;
    private static int assertions, failures;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Encodings = { "srgb", "linear_rec709", "rec709_gamma24" };
    private static readonly string[] Spaces = { "Srgb", "AdobeRgb", "ProPhotoRgb", "LinearRec2020" };

    [STAThread]
    private static int Main()
    {
        const string c1 = @"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
        panel = Assembly.Load("NativePanel");
        frameType = panel.GetType("HostImageSource+Frame", true)!;
        spaceType = panel.GetType("PreviewInputSpace", true)!;
        previewType = panel.GetType("SpatialPreview", true)!;
        Run("Neutral finishing matches legacy preview/export exactly", Neutral);
        Run("Upstream fixture colors reach 16-bit export and 8-bit preview", ReferenceColors);
        Run("Partial strength blends finished result in linear light", PartialStrength);
        Run("Output encodings, alpha, crop and 16-bit precision", EncodingAlphaCrop);
        Run("Unclipped rendered highlights and shadows remain recoverable", Latitude);
        Run("Partial strength preserves renderer latitude and signed Native sRGB", RendererLatitude);
        Run("Show source bypass and invalid renderer/cancellation handling", BypassAndInvalid);
        Run("Parallel preview rows use one immutable finishing snapshot", ParallelSnapshot);
        Run("SciPy micro-contrast fixtures reach preview/export with crop, strength and alpha", MicroContrastFixtures);
        Run("Pointwise finishing precedes full-frame micro-contrast across output encodings", MicroContrastCombined);
        Console.WriteLine($"{assertions} assertions; {failures} failing test groups.");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string label, Action test)
    {
        try { test(); Console.WriteLine("PASS " + label); }
        catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + label + ": " + error.GetBaseException().Message); }
    }
    private static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    private static object Space(string name) => Enum.Parse(spaceType, name);
    private static object Frame(float[] samples, int width, int height, byte[]? codes = null) =>
        Activator.CreateInstance(frameType, width, height, codes ?? Array.Empty<byte>(), Bytes(samples), 16)!;
    private static byte[] Bytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static object Finishing(params (string Key, double Value)[] values) => Finishing(values.ToDictionary(v => "finishing" + v.Key, v => v.Value));
    private static object Finishing(Dictionary<string, double> values)
    {
        var settings = panel.GetType("FinishingSettings", true)!.GetMethod("FromDictionary", Static)!.Invoke(null, new object[] { values })!;
        return Activator.CreateInstance(panel.GetType("FinishingTransform", true)!, settings)!;
    }
    private static BitmapSource Preview(object frame, byte[]? rendered, double strength, string space, string encoding, object? finish,
        bool legacy = false, CancellationToken token = default) =>
        (BitmapSource)previewType.GetMethod(legacy ? "DecodeResult" : "DecodeResultWithFinishing", Static)!
            .Invoke(null, legacy ? new object?[] { frame, rendered, strength, Space(space), encoding, token }
                : new object?[] { frame, rendered, strength, Space(space), encoding, token, finish })!;
    private static BitmapSource Export(object frame, byte[]? rendered, double strength, string space, string encoding, object? finish,
        PixelFormat format, Rectangle crop, bool legacy = false, CancellationToken token = default) =>
        (BitmapSource)previewType.GetMethod(legacy ? "DecodeExport" : "DecodeExportWithFinishing", Static)!
            .Invoke(null, legacy ? new object?[] { frame, rendered, strength, Space(space), encoding, format, crop, token }
                : new object?[] { frame, rendered, strength, Space(space), encoding, format, crop, token, finish })!;
    private static byte[] Pixels(BitmapSource image)
    {
        int stride = image.PixelWidth * image.Format.BitsPerPixel / 8;
        var bytes = new byte[stride * image.PixelHeight]; image.CopyPixels(bytes, stride, 0); return bytes;
    }
    private static ushort[] Codes16(BitmapSource image) => MemoryMarshal.Cast<byte, ushort>(Pixels(image)).ToArray();
    private static double Decode(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static double Encode(double value) => value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
    private static ushort Code16(double value) => (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535);
    private static byte Code8(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static float[] Source(int count)
    {
        var values = new float[count * 4];
        for (int p = 0; p < count; p++)
        { values[p * 4] = (p * 17 % 101) / 100f; values[p * 4 + 1] = (p * 31 % 101) / 100f; values[p * 4 + 2] = (p * 53 % 101) / 100f; values[p * 4 + 3] = (p % 5) / 4f; }
        return values;
    }

    private static void Neutral()
    {
        var source = Source(12); var frame = Frame(source, 4, 3);
        var film = Bytes(Source(12).Select((x, i) => i % 4 == 3 ? x : .9f - x * .7f).ToArray());
        var neutral = Finishing(); var disabled = Finishing(("Active", 0), ("ExposureEv", 4), ("CurveMidtones", .75));
        foreach (var space in Spaces) foreach (var encoding in Encodings) foreach (var strength in new[] { 0d, .27, 1d })
        {
            var baseline = Pixels(Preview(frame, strength == 0 ? null : film, strength, space, encoding, null, legacy: true));
            Check(baseline.SequenceEqual(Pixels(Preview(frame, strength == 0 ? null : film, strength, space, encoding, neutral))), $"Preview neutral mismatch {space}/{encoding}/{strength}");
            Check(baseline.SequenceEqual(Pixels(Preview(frame, strength == 0 ? null : film, strength, space, encoding, disabled))), "Disabled preview differs.");
            foreach (var format in new[] { PixelFormats.Rgb48, PixelFormats.Rgba64 }) foreach (var crop in new[] { new Rectangle(0, 0, 4, 3), new Rectangle(1, 1, 2, 2) })
            {
                var old = Pixels(Export(frame, strength == 0 ? null : film, strength, space, encoding, null, format, crop, legacy: true));
                var current = Export(frame, strength == 0 ? null : film, strength, space, encoding, neutral, format, crop);
                Check(current.IsFrozen && current.Format == format && current.PixelWidth == crop.Width && current.PixelHeight == crop.Height, "Export layout/freeze changed.");
                Check(old.SequenceEqual(Pixels(current)), $"Export neutral mismatch {space}/{encoding}/{strength}/{format}");
                Check(old.SequenceEqual(Pixels(Export(frame, strength == 0 ? null : film, strength, space, encoding, disabled, format, crop))), "Disabled export differs.");
            }
        }
    }

    private static void ReferenceColors()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json")));
        foreach (var test in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (test.GetProperty("name").GetString() is "identity" or "disabled") continue;
            var settings = test.GetProperty("settings").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble());
            var inputs = test.GetProperty("inputs").EnumerateArray().ToArray();
            var expected = test.GetProperty("expected").EnumerateArray().ToArray();
            var rgba = new float[inputs.Length * 4];
            for (int i = 0; i < inputs.Length; i++) { for (int c = 0; c < 3; c++) rgba[i * 4 + c] = (float)inputs[i][c].GetDouble(); rgba[i * 4 + 3] = .5f; }
            var frame = Frame(Source(inputs.Length), inputs.Length, 1); var finish = Finishing(settings);
            var export = Codes16(Export(frame, Bytes(rgba), 1, "Srgb", "srgb", finish, PixelFormats.Rgb48, new Rectangle(0, 0, inputs.Length, 1)));
            var preview = Pixels(Preview(frame, Bytes(rgba), 1, "Srgb", "srgb", finish));
            for (int i = 0; i < inputs.Length; i++) for (int c = 0; c < 3; c++)
            {
                var wanted = expected[i][c].GetDouble();
                Check(Math.Abs(export[i * 3 + c] - Code16(wanted)) <= 1, $"Upstream export {test.GetProperty("name").GetString()} pixel {i}/{c}");
                Check(Math.Abs(preview[i * 4 + 2 - c] - Code8(wanted)) <= 1, $"Upstream preview {test.GetProperty("name").GetString()} pixel {i}/{c}");
            }
        }
    }

    // Analytic baseline conversion is independent of the production LUT lookup.
    // One 16-bit code tolerance covers its interpolation and float transport.
    private static double[] Baseline(string space, float[] source, int i)
    {
        double r = source[i], g = source[i + 1], b = source[i + 2];
        if (space == "Srgb") return new[] { Decode(r), Decode(g), Decode(b) };
        if (space == "AdobeRgb") { r = Math.Pow(Math.Clamp(r, 0, 1), 563d / 256); g = Math.Pow(Math.Clamp(g, 0, 1), 563d / 256); b = Math.Pow(Math.Clamp(b, 0, 1), 563d / 256); return new[] { 1.39828 * r - .39828 * g, g, -.04294 * g + 1.04294 * b }; }
        if (space == "ProPhotoRgb")
        {
            static double P(double v) { v = Math.Clamp(v, 0, 1); return v <= 1d / 32 ? v / 16 : Math.Pow(v, 1.8); }
            r = P(r); g = P(g); b = P(b);
            return new[] { 2.034218153 * r - .727536473 * g - .306681680 * b, -.228798056 * r + 1.231730282 * g - .002932226 * b, -.008555533 * r - .153286575 * g + 1.161842108 * b };
        }
        return new[] { 1.660491002 * r - .587641139 * g - .072849863 * b, -.124550475 * r + 1.132899897 * g - .008349423 * b, -.018150763 * r - .100578898 * g + 1.118729661 * b };
    }
    private static void PartialStrength()
    {
        var source = Source(12); var frame = Frame(source, 4, 3);
        const double contrast = .5;
        var finish = Finishing(("Contrast", contrast));
        foreach (var space in Spaces) foreach (var encoding in Encodings) foreach (var strength in new[] { .1, .5, .9 })
        {
            var rendered = new float[48];
            for (int i = 0; i < 48; i++) rendered[i] = i % 4 == 3 ? .25f : (float)(encoding == "srgb" ? .6 : encoding == "linear_rec709" ? Decode(.6) : Math.Pow(Decode(.6), 1 / 2.4));
            var actual = Codes16(Export(frame, Bytes(rendered), strength, space, encoding, finish, PixelFormats.Rgba64, new Rectangle(0, 0, 4, 3)));
            for (int p = 0; p < 12; p++)
            {
                var baseline = Baseline(space, source, p * 4);
                for (int c = 0; c < 3; c++)
                {
                    var finished = .5 + (.6 - .5) * (1 + contrast);
                    var expected = Encode(baseline[c] + strength * (Decode(finished) - baseline[c]));
                    Check(Math.Abs(actual[p * 4 + c] - Code16(expected)) <= 1, $"Linear partial-strength mismatch {space}/{encoding}/{strength}/{p}/{c}");
                }
                Check(actual[p * 4 + 3] == Code16(source[p * 4 + 3]), "Strength changed alpha.");
            }
        }
        var single = Frame(new[] { .2f, .2f, .2f, 1f }, 1, 1);
        var color = Codes16(Export(single, Bytes(new[] { .6f, .6f, .6f, 1f }), .5, "Srgb", "srgb", finish, PixelFormats.Rgb48, new Rectangle(0, 0, 1, 1)))[0];
        Check(Math.Abs(color - Code16((.2 + .65) / 2)) > 2000, "Strength test does not distinguish encoded-domain blending.");
    }

    private static void EncodingAlphaCrop()
    {
        var source = Source(12); var frame = Frame(source, 4, 3);
        var crop = new Rectangle(1, 1, 2, 2); var finish = Finishing(("Saturation", 0));
        foreach (var encoding in Encodings)
        {
            var rendered = new float[48];
            for (int p = 0; p < 12; p++) for (int c = 0; c < 4; c++)
            {
                var encoded = .1234567 + .013 * p + .18 * c;
                rendered[p * 4 + c] = c == 3 ? 0 : (float)(encoding == "srgb" ? encoded : encoding == "linear_rec709" ? Decode(encoded) : Math.Pow(Decode(encoded), 1 / 2.4));
            }
            foreach (var format in new[] { PixelFormats.Rgb48, PixelFormats.Rgba64 })
            {
                var image = Export(frame, Bytes(rendered), 1, "Srgb", encoding, finish, format, crop);
                var actual = Codes16(image); var n = format == PixelFormats.Rgba64 ? 4 : 3;
                for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++)
                {
                    int p = (y + 1) * 4 + x + 1, q = (y * 2 + x) * n;
                    var luma = .2126 * (.1234567 + .013 * p) + .7152 * (.3034567 + .013 * p) + .0722 * (.4834567 + .013 * p);
                    Check(Math.Abs(actual[q] - Code16(luma)) <= 1 && actual[q] == actual[q + 1] && actual[q] == actual[q + 2], "Encoding/crop/luma mismatch " + encoding);
                    if (n == 4) Check(actual[q + 3] == Code16(source[p * 4 + 3]), "Crop alpha came from rendered data, not source.");
                }
                Check(actual.Where((_, i) => i % n < 3).Any(c => c % 257 != 0), "Export was quantized through an 8-bit buffer.");
            }
            var codes = Enumerable.Range(0, 48).Select(i => (byte)(i % 4 == 3 ? 17 + i : 0)).ToArray();
            var codedFrame = Frame(source, 4, 3, codes); var preview = Pixels(Preview(codedFrame, Bytes(rendered), 1, "Srgb", encoding, finish));
            Check(Enumerable.Range(0, 12).All(p => preview[p * 4 + 3] == codes[p * 4 + 3]), "Preview did not preserve original alpha codes.");
        }
    }

    private static void Latitude()
    {
        var frame = Frame(new[] { .2f, .2f, .2f, 1f }, 1, 1);
        foreach (var encoding in Encodings)
        {
            double encodedOver = 1.3;
            var outValue = (float)(encoding == "srgb" ? encodedOver : encoding == "linear_rec709" ? Decode(encodedOver) : Math.Pow(Decode(encodedOver), 1 / 2.4));
            var actual = Codes16(Export(frame, Bytes(new[] { outValue, outValue, outValue, 1f }), 1, "Srgb", encoding,
                Finishing(("ExposureEv", -2)), PixelFormats.Rgb48, new Rectangle(0, 0, 1, 1)))[0];
            var expected = Code16(Encode(Decode(encodedOver) / 4));
            Check(Math.Abs(actual - expected) <= 1, "Over-white value clipped before recovery: " + encoding);
            Check(actual > Code16(Encode(.25)) + 5000, "Recovery test does not distinguish early clipping.");
        }
        var lifted = Codes16(Export(frame, Bytes(new[] { -.1f, -.1f, -.1f, 1f }), 1, "Srgb", "srgb",
            Finishing(("BlackPoint", -.2)), PixelFormats.Rgb48, new Rectangle(0, 0, 1, 1)))[0];
        Check(Math.Abs(lifted - Code16((-.1 + .2) / 1.2)) <= 1, "Negative value clipped before levels recovery.");
    }

    private static void RendererLatitude()
    {
        var source=new[]{.65f,.65f,.65f,1f};var frame=Frame(source,1,1);
        foreach(var linear in new[]{-.2,1.8}) foreach(var encoding in new[]{"srgb","native_srgb","linear_rec709","native_rec709_gamma24"})
        {
            double encoded=encoding=="linear_rec709"?linear:encoding=="native_srgb"?Math.CopySign(Encode(Math.Abs(linear)),linear)
                :encoding=="native_rec709_gamma24"?Math.CopySign(Math.Pow(Math.Abs(linear),1/2.4),linear):Encode(linear);
            var rendered=Bytes(new[]{(float)encoded,(float)encoded,(float)encoded,1f});
            const double strength=.25;
            var expected=Code16(Encode(Decode(source[0])+strength*(linear-Decode(source[0]))));
            var output=Codes16(Export(frame,rendered,strength,"Srgb",encoding,Finishing(),PixelFormats.Rgb48,new Rectangle(0,0,1,1)));
            Check(output.All(x=>Math.Abs(x-expected)<=1),"Renderer latitude lost before strength: "+encoding+"/"+linear);
            // Active finishing and its identity must agree about the renderer's transfer extension.
            var finished=Codes16(Export(frame,rendered,strength,"Srgb",encoding,Finishing(("ExposureEv",-.5)),PixelFormats.Rgb48,new Rectangle(0,0,1,1)));
            var expectedFinished=Code16(Encode(Decode(source[0])+strength*(linear*Math.Pow(2,-.5)-Decode(source[0]))));
            Check(finished.All(x=>Math.Abs(x-expectedFinished)<=1),"Signed transfer changed during finishing: "+encoding);
        }
    }

    private static void BypassAndInvalid()
    {
        var source = Source(12); var codes = Enumerable.Range(0, 48).Select(i => (byte)(i * 5)).ToArray();
        var frame = Frame(source, 4, 3, codes); var finish = Finishing(("ExposureEv", 4), ("CurveMidtones", 1));
        foreach (var encoding in Encodings)
        {
            Check(Pixels(Preview(frame, null, 0, "Srgb", encoding, finish)).SequenceEqual(codes), "Source compare changed original code buffer.");
            foreach (var space in Spaces)
            {
                var actual = Export(frame, null, 0, space, encoding, finish, PixelFormats.Rgba64, new Rectangle(0, 0, 4, 3));
                var legacy = Export(frame, null, 0, space, encoding, null, PixelFormats.Rgba64, new Rectangle(0, 0, 4, 3), legacy: true);
                Check(Pixels(actual).SequenceEqual(Pixels(legacy)), "Strength zero applied finishing: " + space);
            }
        }
        var invalid = Source(12); invalid[3] = float.NaN; // Renderer alpha is deliberately ignored.
        Export(frame, Bytes(invalid), 1, "Srgb", "srgb", finish, PixelFormats.Rgba64, new Rectangle(0, 0, 4, 3));
        foreach (var v in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            invalid[0] = v;
            Throws<InvalidDataException>(() => Export(frame, Bytes(invalid), 1, "Srgb", "srgb", finish, PixelFormats.Rgb48, new Rectangle(0, 0, 4, 3)), "Invalid renderer pixel accepted.");
        }
        var canceled = new CancellationToken(true);
        Throws<OperationCanceledException>(() => Export(frame, Bytes(source), 1, "Srgb", "srgb", finish, PixelFormats.Rgb48, new Rectangle(0, 0, 4, 3), token: canceled), "Export cancellation ignored.");
        Throws<OperationCanceledException>(() => Preview(frame, Bytes(source), 1, "Srgb", "srgb", finish, token: canceled), "Preview cancellation ignored.");
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        assertions++;
        try { action(); } catch (Exception e) when (e.GetBaseException() is T) { return; }
        throw new Exception(message);
    }

    private static void ParallelSnapshot()
    {
        var settings = new Dictionary<string, double> { ["finishingSaturation"] = 0, ["finishingContrast"] = .2 };
        var captured = Finishing(settings);
        settings["finishingSaturation"] = 2; settings["finishingContrast"] = -.5;
        var later = Finishing(settings);
        var sample = new[] { .25f, .5f, .75f, .375f };
        var one = Frame(sample, 1, 1);
        var expected = Pixels(Preview(one, Bytes(sample), .75, "Srgb", "srgb", captured));
        var changed = Pixels(Preview(one, Bytes(sample), .75, "Srgb", "srgb", later));
        Check(!expected.SequenceEqual(changed), "Snapshot mutation test is not sensitive.");
        const int width = 2000, height = 2001; // Crosses production's 4 MP threshold; uneven worker ranges.
        var data = new float[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) sample.CopyTo(data, i);
        var frame = Frame(data, width, height);
        var actual = Pixels(Preview(frame, Bytes(data), .75, "Srgb", "srgb", captured));
        Check(actual.Length == width * height * 4, "Parallel preview has wrong buffer length.");
        for (int i = 0; i < actual.Length; i++)
            if (actual[i] != expected[i % 4]) throw new Exception($"Parallel preview changed channel at {i}.");
        assertions++;
        Check(Pixels(Preview(one, Bytes(sample), .75, "Srgb", "srgb", captured)).SequenceEqual(expected), "Finishing snapshot changed after parallel execution.");

        // Small frames are converted in parallel too (drag frames, screen-size frames). Every pixel different,
        // uneven worker ranges: the bytes must equal each row converted alone, on one thread (a one-row frame).
        const int smallWidth = 1301, smallHeight = 701;
        var random = new Random(20261121);
        var source = new float[smallWidth * smallHeight * 4];
        var rendered = new float[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = i % 4 == 3 ? 1 : (float)random.NextDouble();
            rendered[i] = i % 4 == 3 ? 1 : (float)(random.NextDouble() * 1.1 - .05);
        }
        var toned = Finishing(("Contrast", .2), ("Saturation", .8), ("ShadowStrength", .1), ("HighlightStrength", .1));
        var whole = Pixels(Preview(Frame(source, smallWidth, smallHeight), Bytes(rendered), .75, "ProPhotoRgb", "srgb", toned));
        int rowBytes = smallWidth * 4;
        for (int y = 0; y < smallHeight; y += 7)
        {
            var rowSource = source.AsSpan(y * rowBytes, rowBytes).ToArray();
            var rowRendered = rendered.AsSpan(y * rowBytes, rowBytes).ToArray();
            var row = Pixels(Preview(Frame(rowSource, smallWidth, 1), Bytes(rowRendered), .75, "ProPhotoRgb", "srgb", toned));
            if (!row.AsSpan().SequenceEqual(whole.AsSpan(y * rowBytes, rowBytes))) throw new Exception($"Parallel small preview differs from the single-thread row {y}.");
        }
        assertions++;
        // An invalid renderer frame makes every worker fail at once; the caller must still get the plain error.
        var invalid = new float[rendered.Length];
        Array.Fill(invalid, float.NaN);
        try
        {
            Preview(Frame(source, smallWidth, smallHeight), Bytes(invalid), .75, "ProPhotoRgb", "srgb", toned);
            throw new Exception("Invalid small frame was accepted.");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { assertions++; }
    }

    private sealed record MicroCase(int Width, int Height, double Amount, float[] Source, float[] Expected);
    private static MicroCase[] MicroCases() => JsonSerializer.Deserialize<MicroCase[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "microcontrast-fixtures.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    private static Rectangle InnerCrop(int width, int height) =>
        new(width > 3 ? 1 : 0, height > 3 ? 1 : 0, width > 3 ? width - 2 : width, height > 3 ? height - 2 : height);

    private static void MicroContrastFixtures()
    {
        int referenceCases = 0, distinguishingChannels = 0;
        foreach (var fixture in MicroCases())
        {
            referenceCases++;
            var source = Source(fixture.Width * fixture.Height);
            var codes = new byte[source.Length];
            for (int p = 0; p < source.Length / 4; p++) codes[p * 4 + 3] = (byte)((p * 41 + 17) % 256);
            var frame = Frame(source, fixture.Width, fixture.Height, codes);
            var rendered = Bytes(fixture.Source);
            var before = (byte[])rendered.Clone();
            var finish = Finishing(("MicroContrast", fixture.Amount));
            foreach (var strength in new[] { .37, 1d })
            {
                if (fixture.Amount == 0)
                {
                    Check(Pixels(Preview(frame, rendered, strength, "Srgb", "srgb", finish)).SequenceEqual(
                        Pixels(Preview(frame, rendered, strength, "Srgb", "srgb", null, legacy: true))), "Micro zero preview changed legacy pixels.");
                    Check(Pixels(Export(frame, rendered, strength, "Srgb", "srgb", finish, PixelFormats.Rgba64,
                        new Rectangle(0, 0, fixture.Width, fixture.Height))).SequenceEqual(Pixels(Export(frame, rendered, strength,
                        "Srgb", "srgb", null, PixelFormats.Rgba64, new Rectangle(0, 0, fixture.Width, fixture.Height), legacy: true))), "Micro zero export changed legacy pixels.");
                    continue;
                }
                foreach (var space in Spaces)
                    VerifyMicroPixels(frame, source, codes, rendered, fixture.Expected, fixture.Width, fixture.Height,
                        strength, space, "srgb", finish);
            }
            Check(Pixels(Preview(frame, null, 0, "Srgb", "srgb", finish)).SequenceEqual(codes), "Micro affected Show source.");
            Check(rendered.SequenceEqual(before), "Micro mutated the renderer buffer.");
            for (int i = 0; i < fixture.Expected.Length; ++i)
                if (i % 4 < 3 && Math.Abs(Code16(fixture.Expected[i]) - Code16(fixture.Source[i])) > 100) distinguishingChannels++;
        }
        Check(referenceCases == 25 && distinguishingChannels > 100, "Micro integration fixtures must detect an omitted spatial pass.");
        var sample = MicroCases().Last();
        var frameForErrors = Frame(Source(sample.Width * sample.Height), sample.Width, sample.Height);
        var active = Finishing(("MicroContrast", 1));
        Throws<InvalidDataException>(() => Preview(frameForErrors, null, 1, "Srgb", "srgb", active), "Missing spatial pixels accepted.");
        Throws<InvalidDataException>(() => Preview(frameForErrors, new byte[4], 1, "Srgb", "srgb", active), "Short spatial pixels accepted.");
        var invalid = (float[])sample.Source.Clone(); invalid[0] = float.NaN;
        Throws<InvalidDataException>(() => Preview(frameForErrors, Bytes(invalid), 1, "Srgb", "srgb", active), "Micro accepted invalid renderer RGB.");
        var canceled = new CancellationToken(true);
        Throws<OperationCanceledException>(() => Preview(frameForErrors, Bytes(sample.Source), 1, "Srgb", "srgb", active, token: canceled), "Micro preview ignored cancellation.");
        Throws<OperationCanceledException>(() => Export(frameForErrors, Bytes(sample.Source), 1, "Srgb", "srgb", active,
            PixelFormats.Rgba64, InnerCrop(sample.Width, sample.Height), token: canceled), "Micro export ignored cancellation.");
        var disabled = Finishing(("Active", 0), ("MicroContrast", 1));
        Check(Pixels(Preview(frameForErrors, Bytes(sample.Source), 1, "Srgb", "srgb", disabled)).SequenceEqual(
            Pixels(Preview(frameForErrors, Bytes(sample.Source), 1, "Srgb", "srgb", null, legacy: true))), "Disabled micro is not exact legacy bypass.");
    }

    private static void VerifyMicroPixels(object frame, float[] source, byte[]? codes, byte[] rendered, float[] expected,
        int width, int height, double strength, string space, string encoding, object finish)
    {
        var full = new Rectangle(0, 0, width, height);
        var crop = InnerCrop(width, height);
        var preview = Pixels(Preview(frame, rendered, strength, space, encoding, finish));
        foreach (var format in new[] { PixelFormats.Rgb48, PixelFormats.Rgba64 })
        {
            int channels = format == PixelFormats.Rgba64 ? 4 : 3;
            var all = Codes16(Export(frame, rendered, strength, space, encoding, finish, format, full));
            var cropped = Codes16(Export(frame, rendered, strength, space, encoding, finish, format, crop));
            for (int p = 0; p < width * height; ++p)
            {
                var baseline = strength >= 1 ? new double[3] : Baseline(space, source, p * 4);
                for (int c = 0; c < 3; ++c)
                {
                    double value = expected[p * 4 + c];
                    if (strength < 1) value = Encode(baseline[c] + strength * (Decode(value) - baseline[c]));
                    Check(Math.Abs(all[p * channels + c] - Code16(value)) <= 1,
                        $"Micro 16-bit mismatch {width}x{height}/{space}/{encoding}/{strength}/{p}/{c}");
                    Check(Math.Abs(preview[p * 4 + 2 - c] - Code8(value)) <= 1,
                        $"Micro 8-bit mismatch {width}x{height}/{space}/{encoding}/{strength}/{p}/{c}");
                }
                if (channels == 4) Check(all[p * 4 + 3] == Code16(source[p * 4 + 3]), "Micro export alpha came from rendered data.");
                byte alpha = codes is null ? (byte)MathF.Round(255 * Math.Clamp(source[p * 4 + 3], 0, 1)) : codes[p * 4 + 3];
                Check(preview[p * 4 + 3] == alpha, "Micro preview alpha changed.");
            }
            for (int y = 0; y < crop.Height; ++y) for (int x = 0; x < crop.Width; ++x) for (int c = 0; c < channels; ++c)
                Check(cropped[(y * crop.Width + x) * channels + c] == all[((y + crop.Y) * width + x + crop.X) * channels + c],
                    "Crop must extract the fully processed frame, including surrounding pixels' blur contribution.");
            if (width * height > 1) Check(all.Where((_, i) => i % channels < 3).Any(v => v % 257 != 0), "Micro export lost 16-bit precision.");
        }
    }

    private static void MicroContrastCombined()
    {
        const int width = 13, height = 9;
        var source = Source(width * height);
        var frame = Frame(source, width, height); // no original 8-bit alpha: test float-alpha path too
        int orderSensitive = 0, cropSensitive = 0;
        foreach (string encoding in Encodings)
        {
            var rendered = new float[source.Length];
            for (int p = 0; p < width * height; ++p) for (int c = 0; c < 4; ++c)
            {
                double srgb = .08 + .8 * (.5 + .5 * Math.Sin(p * .73 + c * 1.6));
                rendered[p * 4 + c] = c == 3 ? float.NaN : (float)(encoding == "srgb" ? srgb
                    : encoding == "linear_rec709" ? Decode(srgb) : Math.Pow(Decode(srgb), 1 / 2.4));
            }
            var encoded = new float[rendered.Length];
            for (int i = 0; i < rendered.Length; ++i)
                encoded[i] = i % 4 == 3 ? 0 : (float)(encoding == "srgb" ? rendered[i]
                    : Encode(encoding == "linear_rec709" ? rendered[i] : Math.Pow(Math.Max(0, rendered[i]), 2.4)));
            foreach (double amount in new[] { -1d, .4, 1d })
            {
                // Analytic nonlinear pointwise oracle, then direct 2D Gaussian
                // convolution. No production transform/helper supplies expected pixels.
                var pointwise = PointwiseOracle(encoded);
                var expected = MicroOracle(pointwise, width, height, amount);
                var wrongOrder = PointwiseOracle(MicroOracle(encoded, width, height, amount));
                for (int i = 0; i < expected.Length; ++i)
                    if (i % 4 < 3 && Math.Abs(Code16(expected[i]) - Code16(wrongOrder[i])) > 100) orderSensitive++;
                var crop = InnerCrop(width, height);
                var croppedInput = new float[crop.Width * crop.Height * 4];
                for (int y = 0; y < crop.Height; ++y)
                    Array.Copy(pointwise, ((y + crop.Y) * width + crop.X) * 4, croppedInput, y * crop.Width * 4, crop.Width * 4);
                var wrongCrop = MicroOracle(croppedInput, crop.Width, crop.Height, amount);
                for (int y = 0; y < crop.Height; ++y) for (int x = 0; x < crop.Width; ++x)
                    if (Math.Abs(Code16(expected[((y + crop.Y) * width + x + crop.X) * 4]) - Code16(wrongCrop[(y * crop.Width + x) * 4])) > 100) cropSensitive++;
                var finish = Finishing(("Gamma", 1.7), ("Contrast", .2), ("Saturation", .65), ("MicroContrast", amount));
                foreach (var strength in new[] { .22, 1d }) foreach (var space in Spaces)
                    VerifyMicroPixels(frame, source, null, Bytes(rendered), expected, width, height, strength, space, encoding, finish);
            }
        }
        Check(orderSensitive > 100, "Combined fixture does not distinguish incorrect micro-before-pointwise order.");
        Check(cropSensitive > 100, "Crop fixture does not distinguish incorrect crop-before-filter order.");
    }

    private static float[] PointwiseOracle(float[] source)
    {
        var result = (float[])source.Clone();
        for (int i = 0; i < source.Length; i += 4)
        {
            double Tone(float v) => .5 + ((v > 0 ? Math.Pow(v, 1 / 1.7) : v) - .5) * 1.2;
            double r = Tone(source[i]), g = Tone(source[i + 1]), b = Tone(source[i + 2]);
            double l = .2126 * r + .7152 * g + .0722 * b;
            result[i] = (float)(l + .65 * (r - l)); result[i + 1] = (float)(l + .65 * (g - l)); result[i + 2] = (float)(l + .65 * (b - l));
        }
        return result;
    }

    // Independent scalar 2D reference; production uses separable row passes.
    private static float[] MicroOracle(float[] source, int width, int height, double amount)
    {
        var result = (float[])source.Clone();
        double[] weights = Enumerable.Range(-5, 11).Select(i => Math.Exp(-i * i / (2 * 1.5 * 1.5))).ToArray();
        double total = weights.Sum(); for (int i = 0; i < weights.Length; ++i) weights[i] /= total;
        int Reflect(int i, int size) { while (i < 0 || i >= size) i = i < 0 ? -i - 1 : 2 * size - 1 - i; return i; }
        double Luma(int x, int y) { int i = (y * width + x) * 4; return .2126 * source[i] + .7152 * source[i + 1] + .0722 * source[i + 2]; }
        for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x)
        {
            double blurred = 0;
            for (int dy = -5; dy <= 5; ++dy) for (int dx = -5; dx <= 5; ++dx)
                blurred += weights[dy + 5] * weights[dx + 5] * Luma(Reflect(x + dx, width), Reflect(y + dy, height));
            double detail = Luma(x, y) - blurred;
            for (int c = 0; c < 3; ++c) result[(y * width + x) * 4 + c] = (float)(source[(y * width + x) * 4 + c] + amount * detail);
        }
        return result;
    }
}
