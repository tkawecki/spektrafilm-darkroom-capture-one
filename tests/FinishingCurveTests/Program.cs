using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int assertions;
    [STAThread]
    private static void Main(string[] args)
    {
        var original = ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(.17320508075688773, .31),
            new FinishingPoint(.743, .81), new FinishingPoint(1, 1));
        string text = FinishingCurveStorage.Serialize(original);
        var parsed = FinishingCurveStorage.Parse(text);
        Check(original.SequenceEqual(parsed), "Arbitrary finishing curve points must survive storage without snapping X");
        Check(parsed == FinishingCurveStorage.Parse(text), "Repeated parses retain immutable array/cache identity");
        Check(parsed == FinishingCurveStorage.Parse(" [ [0, 0], [0.17320508075688773, 0.31], [0.743, 0.81], [1, 1] ]"), "Equivalent formatting is interned");
        Check(parsed == FinishingCurveStorage.Parse("[[1,1],[0.743,0.81],[0,0],[0.17320508075688773,0.31]]"), "Unsorted inputs canonicalize without losing coordinates");
        Check(FinishingCurveStorage.Parse("").IsDefault && FinishingCurveStorage.Parse("  ").IsDefault, "Empty means no arbitrary override");
        Check(FinishingCurveStorage.Serialize(default) == "" && FinishingCurveStorage.Serialize(ImmutableArray<FinishingPoint>.Empty) == "", "Absent curve serializes as no override");
        Check(FinishingCurveStorage.Parse("[[0,0],[1,1]]") == FinishingCurveStorage.Identity, "Identity is shared");
        string[] malformed = { "[]", "[[0,0]]", "[0,1]", "[[0,0],[0,1]]", "[[0,0],[1,1,2]]", "[[0,0],[1,null]]",
            "[[0,0],[1,\"1\"]]", "[[0,0],[1,1.001]]", "[[-0.01,0],[1,1]]", "[[0,0],[1e999,1]]", "NaN", "{\"points\":[]}", "[[0,0],[1,1]]," };
        foreach (string value in malformed) Throws<FormatException>(() => FinishingCurveStorage.Parse(value), "Invalid data rejected: " + value);
        Throws<FormatException>(() => FinishingCurveStorage.Parse(new string('[', 32769)), "Oversized storage rejected");
        Throws<ArgumentException>(() => FinishingCurveStorage.Serialize(ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(1, double.NaN))), "Serialize rejects nonfinite output");
        Throws<ArgumentException>(() => FinishingCurveStorage.Serialize(ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(double.PositiveInfinity, 1))), "Serialize rejects nonfinite input");
        var full = Enumerable.Range(0, 64).Select(i => new FinishingPoint(i / 63d, i / 63d)).ToImmutableArray();
        Check(FinishingCurveStorage.Parse(FinishingCurveStorage.Serialize(full)).SequenceEqual(full), "64 points preserved");
        Throws<ArgumentException>(() => FinishingCurveStorage.Serialize(full.Add(new(.12345, .3))), "65 points rejected");
        Throws<FormatException>(() => FinishingCurveStorage.Parse("[" + string.Join(",", Enumerable.Range(0,65).Select(i => $"[{(i / 64d).ToString(CultureInfo.InvariantCulture)},0]")) + "]"), "Parser rejects 65 points");

        var added = FinishingCurveData.Add(original, new(.4567890123456789, .12345678901234568));
        Check(added.Length == 5 && added[2] == new FinishingPoint(.4567890123456789, .12345678901234568), "Click inserts exact arbitrary X/Y in sorted order");
        Check(original.Length == 4 && original[1].X == .17320508075688773, "Editing never mutates caller's original");
        Throws<ArgumentException>(() => FinishingCurveData.Add(original, new(.743, .2)), "Click cannot create duplicate X");
        Throws<ArgumentException>(() => FinishingCurveData.Add(full, new(.12345, .4)), "Cannot click-add past 64");
        Check(FinishingCurveData.Move(original, 1, original[1]) == original, "Unchanged drag retains array identity");
        var moved = FinishingCurveData.Move(original, 1, new(.25, .6));
        Check(moved[1] == new FinishingPoint(.25, .6), "Arbitrary drag is precise");
        var constrained = FinishingCurveData.Move(original, 1, new(2, -1));
        Check(constrained[1].X == Math.BitDecrement(original[2].X) && constrained[1].Y == 0, "Drag respects adjacent X and 0..1 bounds without fixed grid quantization");
        var endpoint = FinishingCurveData.Move(original, 0, new(-1, 2));
        Check(endpoint[0] == new FinishingPoint(0, 1), "Endpoint drag clamps to domain");
        Check(FinishingCurveData.Remove(added, 2).SequenceEqual(original), "Remove restores original points");
        Check(FinishingCurveData.Remove(FinishingCurveStorage.Identity, 0) == FinishingCurveStorage.Identity, "Minimum two points preserved");
        Throws<ArgumentException>(() => FinishingCurveData.Move(original, 1, new(double.NaN, .2)), "Invalid drag rejected");

        var rng = new Random(731);
        var curves = new List<ImmutableArray<FinishingPoint>> { original, FinishingCurveStorage.Identity, default,
            ImmutableArray.Create(new FinishingPoint(.12, .3), new FinishingPoint(.57, .85), new FinishingPoint(.92, .6)) };
        for (int n = 2; n <= 64; ++n)
        {
            var points = Enumerable.Range(0, n).Select(i => new FinishingPoint((i + rng.NextDouble() * .9) / n, rng.NextDouble())).ToImmutableArray();
            Check(FinishingCurveStorage.Parse(FinishingCurveStorage.Serialize(points)).SequenceEqual(points), "Exact double roundtrip at every supported count");
            curves.Add(points);
        }
        double maximumDifference = 0;
        foreach (var curve in curves)
        {
            var graph = FinishingCurveData.Preview(curve, 1024);
            var transform = new FinishingTransform(new FinishingSettings { Curve = curve });
            for (int i = 0; i < graph.Length; ++i)
            {
                double r = i / 1023d, g = r, b = r;
                transform.Apply(ref r, ref g, ref b);
                maximumDifference = Math.Max(maximumDifference, Math.Abs(graph[i].Y - r));
                Check(graph[i].X == i / 1023d && graph[i].Y == r && double.IsFinite(r) && r >= 0 && r <= 1,
                    "Graph must show the actual bounded 1024-entry finishing LUT");
            }
        }
        var priorCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
        try { Check(FinishingCurveStorage.Serialize(original) == text, "Storage is culture invariant"); }
        finally { CultureInfo.CurrentCulture = priorCulture; }
        Parallel.For(0, 1000, _ => {
            if (FinishingCurveStorage.Parse(text) != parsed) throw new InvalidOperationException("Concurrent cache identity changed.");
        });
        // Construct and lay out the real editor offscreen, without Show/ShowDialog
        // or a PresentationSource. This also tests numeric-field precision.
        var editorType = typeof(FinishingCurveEditor).GetNestedType("CurveWindow", BindingFlags.NonPublic)!;
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var dialog = (Window)Activator.CreateInstance(editorType, privateInstance, null,
            new object[] { "Master curve", original }, CultureInfo.InvariantCulture)!;
        var input = (TextBox)editorType.GetField("input", privateInstance)!.GetValue(dialog)!;
        var output = (TextBox)editorType.GetField("output", privateInstance)!.GetValue(dialog)!;
        var commit = editorType.GetMethod("CommitNumbers", privateInstance)!;
        var property = editorType.GetProperty("Points", privateInstance)!;
        var selectedField = editorType.GetField("selected", privateInstance)!;
        var redraw = editorType.GetMethod("Redraw", privateInstance)!;
        Check((bool)commit.Invoke(dialog, null)! && (ImmutableArray<FinishingPoint>)property.GetValue(dialog)! == original,
            "Unchanged numeric fields must preserve the exact imported array");
        selectedField.SetValue(dialog, 1);
        redraw.Invoke(dialog, null);
        // Both decimal marks, whatever the Windows number format (a decimal comma refused "47.125" before).
        foreach (var culture in new[] { "en-US", "pl-PL", "de-DE" })
        {
            var saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            try
            {
                Check(PercentText.TryParse("47.125", out var dot) && dot == 47.125, "Dot decimal accepted in " + culture);
                Check(PercentText.TryParse("47,125", out var comma) && comma == 47.125, "Comma decimal accepted in " + culture);
                Check(PercentText.TryParse(" 100 ", out var whole) && whole == 100, "Whole percentage accepted in " + culture);
                Check(!PercentText.TryParse("100.5", out _) && !PercentText.TryParse("-1", out _) && !PercentText.TryParse("1.2.3", out _)
                    && !PercentText.TryParse("abc", out _), "Out-of-range or malformed percentage refused in " + culture);
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }
        output.Text = "47.125";
        Check((bool)commit.Invoke(dialog, null)!, "Precise output percentage accepted");
        var afterOutput = (ImmutableArray<FinishingPoint>)property.GetValue(dialog)!;
        Check(afterOutput[1].X == original[1].X && afterOutput[1].Y == .47125,
            "Changing output preserves arbitrary X bit-exactly");
        input.Text = "75";
        Check(!(bool)commit.Invoke(dialog, null)! && (ImmutableArray<FinishingPoint>)property.GetValue(dialog)! == afterOutput,
            "Numeric edits cannot cross an adjacent point or corrupt the model");
        redraw.Invoke(dialog, null);
        input.Text = "18.123456789";
        Check((bool)commit.Invoke(dialog, null)!, "Arbitrary input percentage accepted");
        var afterInput = (ImmutableArray<FinishingPoint>)property.GetValue(dialog)!;
        Check(afterInput[1].X == 18.123456789 / 100 && afterInput[1].Y == .47125,
            "Input editing preserves output and exact entered X");
        Check(PresentationSource.FromVisual(dialog) is null && !dialog.IsVisible, "Editor test never creates a displayed OS window");
        var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(600, 600));
        content.Arrange(new Rect(0, 0, 600, 600));
        content.UpdateLayout();
        var target = new RenderTargetBitmap(600, 600, 96, 96, PixelFormats.Pbgra32);
        target.Render(content);
        if (args.Length > 0)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var file = File.Create(Path.Combine(Path.GetDirectoryName(args[0])!, "finishing-curve-editor-offscreen.png"));
            encoder.Save(file);
        }
        dialog.Close();
        string report = JsonSerializer.Serialize(new { passed = true, assertions, graphCurves = curves.Count,
            graphSamples = curves.Count * 1024, graphMaximumDifference = maximumDifference,
            arbitraryX = parsed[1].X, exactDoubleRoundtrip = true, concurrentCacheIdentity = true,
            osWindowsOpened = 0 }, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length > 0) File.WriteAllText(args[0], report);
        Console.WriteLine(report);
    }
    private static void Check(bool ok, string message)
    {
        ++assertions;
        if (!ok) throw new InvalidOperationException(message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { ++assertions; return; }
        throw new InvalidOperationException(message);
    }
}
