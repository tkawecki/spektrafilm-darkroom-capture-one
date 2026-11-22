// Compares Viewer pixels from the renderer's packed blend (--display-strength) with the panel's
// managed conversion of the float frame, byte by byte.
// Usage: PackCheck <NativePanel.dll> <runtime> <source.f32> <w> <h>
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows.Media.Imaging;
using Expression = System.Linq.Expressions.Expression;

internal static class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object? Call(object target, string name, params object?[] args)
    {
        var type = target as Type ?? target.GetType();
        var method = type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        try { return method.Invoke(target is Type ? null : target, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
    [STAThread]
    static int Main(string[] a)
    {
        const string c1 = @"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
        var panel = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(a[0]));
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(a[1]));
        Environment.SetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG", Path.Combine(Path.GetTempPath(), "packcheck-" + Guid.NewGuid().ToString("N"), "panel.log"));
        int fw = int.Parse(a[3]), fh = int.Parse(a[4]);
        var full = File.ReadAllBytes(a[2]);
        var lookType = panel.GetType("LookSettings", true)!;
        var preview = panel.GetType("SpatialPreview", true)!;
        var frameType = panel.GetType("HostImageSource+Frame", true)!;
        var spaceType = panel.GetType("PreviewInputSpace", true)!;
        var rendererType = panel.GetType("OpticalRenderer", true)!;
        int failures = 0;
        foreach (var (w, h) in new[] { (800, 600), (fw, fh) })
        {
            // Crop the top-left w x h block of the source.
            var src = new byte[w * h * 16];
            for (var y = 0; y < h; y++) Buffer.BlockCopy(full, y * fw * 16, src, y * w * 16, w * 16);
            var frame = Activator.CreateInstance(frameType, w, h, Array.Empty<byte>(), src, 16)!;
            foreach (var (id, spaceName) in new[] { ("srgb", "Srgb"), ("adobe_rgb", "AdobeRgb"), ("prophoto", "ProPhotoRgb"), ("linear_rec2020", "LinearRec2020") })
            {
                var look = System.Text.Json.JsonSerializer.Deserialize("{\"Film\":\"kodak_portra_400\",\"Paper\":\"kodak_portra_endura\",\"Mode\":\"print\",\"SpectralMethod\":\"hanatos2025\",\"Exposure\":0.3,\"PrintExposure\":0,\"Strength\":1,\"Development\":{}}", lookType)!;
                var holder = new StrongBox<object>(look);
                var callback = Expression.Lambda(typeof(Func<>).MakeGenericType(lookType), Expression.Convert(Expression.Field(Expression.Constant(holder), "Value"), lookType)).Compile();
                using var view = (IDisposable)Call(preview, "BuildForPanel", new object(), callback, new Func<Guid, bool>(_ => true))!;
                Call(view, "LoadSettings", new object?[] { null });
                var args = (string[])Call(view, "RendererOptions", look, id, "srgb", false, false)!;
                var finishing = Activator.CreateInstance(panel.GetType("FinishingTransform", true)!, new[] { Call(view, "CaptureFinishingSettings") })!;
                var geometry = Call(view, "CaptureGeometrySettings");
                var paper = preview.GetField("paperDecoration", All)!.GetValue(view);
                using var renderer = (IDisposable)Activator.CreateInstance(rendererType, All, null, new object[] { false, true }, null)!;
                var floats = ((Task<byte[]>)Call(renderer, "RenderAsync", w, h, src, args, CancellationToken.None, false)!).GetAwaiter().GetResult();
                foreach (var strength in new[] { 0.25, 0.521739130434783, 0.9, 1.0 })
                {
                    var managed = (BitmapSource)Call(preview, "DecodeDecoratedPreview", frame, floats, strength, Enum.Parse(spaceType, spaceName), "native_srgb", CancellationToken.None, finishing, geometry, paper)!;
                    var expected = new byte[w * h * 4]; managed.CopyPixels(expected, w * 4, 0);
                    var packedArgs = (string[])Call(preview, "WithDisplayStrength", args, strength)!;
                    var packed = ((Task<byte[]>)Call(renderer, "RenderAsync", w, h, src, packedArgs, CancellationToken.None, true)!).GetAwaiter().GetResult();
                    int diff = 0, max = 0;
                    for (var i = 0; i < expected.Length; i++) { var d = Math.Abs(expected[i] - packed[i]); if (d > 0) { diff++; max = Math.Max(max, d); } }
                    Console.WriteLine($"{w}x{h} {id,-15} strength {strength:0.######}: {(diff == 0 ? "identical" : $"{diff} bytes differ, max {max}")}");
                    if (diff != 0) failures++;
                }
            }
        }
        Console.WriteLine(failures == 0 ? "PASS: renderer blend equals managed conversion" : $"FAIL: {failures} cases differ");
        return failures == 0 ? 0 : 1;
    }
}
