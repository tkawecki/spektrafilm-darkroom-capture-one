using System.Text.Json;
using System.Collections.Immutable;

var failures = new List<string>();
var count = 0;
void Check(bool condition, string message) { count++; if (!condition) failures.Add(message); }
var neutral = new FinishingTransform(new FinishingSettings());
Check(neutral.IsIdentity, "Default transform is identity.");
foreach (var value in new[] { -0d, 0d, .0001, .2, .5, 1d, -1d, 2d, double.MinValue, double.MaxValue })
{
    double r = value, g = value, b = value;
    neutral.Apply(ref r, ref g, ref b);
    Check(BitConverter.DoubleToInt64Bits(r) == BitConverter.DoubleToInt64Bits(value) &&
          BitConverter.DoubleToInt64Bits(g) == BitConverter.DoubleToInt64Bits(value) &&
          BitConverter.DoubleToInt64Bits(b) == BitConverter.DoubleToInt64Bits(value), $"Bit identity for {value:R}.");
}
using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json")));
double maxError = 0;
var fixturePixels = 0;
foreach (var test in fixtures.RootElement.GetProperty("cases").EnumerateArray())
{
    var values = test.GetProperty("settings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
    var transform = new FinishingTransform(FinishingSettings.FromDictionary(values));
    var inputs = test.GetProperty("inputs").EnumerateArray().ToArray();
    var expected = test.GetProperty("expected").EnumerateArray().ToArray();
    for (int i = 0; i < inputs.Length; i++)
    {
        var rgb = inputs[i].EnumerateArray().Select(x => x.GetDouble()).ToArray();
        transform.Apply(ref rgb[0], ref rgb[1], ref rgb[2]);
        for (int c = 0; c < 3; c++)
        {
            var wanted = expected[i][c].GetDouble();
            var error = Math.Abs(rgb[c] - wanted);
            maxError = Math.Max(error, maxError);
            Check(error <= 2e-12 * Math.Max(1, Math.Abs(wanted)), $"{test.GetProperty("name").GetString()} pixel {i} channel {c}: {rgb[c]:R} != {wanted:R}.");
        }
        fixturePixels++;
    }
}
var extreme = new FinishingTransform(new FinishingSettings { ExposureEv = 4, GainRedEv = 2, Gamma = .2, Contrast = 1, Saturation = 2, Vibrance = 2 });
foreach (var value in new[] { double.MinValue, -100d, -1d, 0d, 1d, 100d, double.MaxValue })
{
    double r = value, g = -value, b = value;
    extreme.Apply(ref r, ref g, ref b);
    Check(double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b), "Finite result at numeric extremes.");
}
var pivot = new FinishingTransform(new FinishingSettings { Contrast = 1 });
double pr = .5, pg = .5, pb = .5;
pivot.Apply(ref pr, ref pg, ref pb);
Check(pr == .5 && pg == .5 && pb == .5, "Contrast preserves encoded pivot.");
var latitude = new FinishingTransform(new FinishingSettings { Contrast = .5 });
double lr = -.2, lg = .5, lb = 1.2;
latitude.Apply(ref lr, ref lg, ref lb);
Check(lr < 0 && lb > 1, "No implicit clipping when no point curve is active.");
var invalid = FinishingSettings.FromDictionary(new Dictionary<string, double> { ["finishingGamma"] = double.NaN, ["finishingSaturation"] = double.PositiveInfinity, ["finishingContrast"] = 100 });
Check(invalid.Gamma == 1 && invalid.Saturation == 1 && invalid.Contrast == 1, "Nonfinite settings fallback; finite out-of-range settings clamp.");
var immutable = new FinishingTransform(new FinishingSettings { ExposureEv = .3, Contrast = .2, Saturation = 1.2, ShadowStrength = .1 });
double er = .2, eg = .4, eb = .8;
immutable.Apply(ref er, ref eg, ref eb);
var parallelFailures = 0;
Parallel.For(0, 10000, _ => { double r = .2, g = .4, b = .8; immutable.Apply(ref r, ref g, ref b); if (r != er || g != eg || b != eb) Interlocked.Increment(ref parallelFailures); });
Check(parallelFailures == 0, "Concurrent calls are deterministic.");
var endpoints = new FinishingTransform(new FinishingSettings { Curve = ImmutableArray.Create(new FinishingPoint(0, .1), new FinishingPoint(1, .9)) });
double cr = -2, cg = .5, cb = 2;
endpoints.Apply(ref cr, ref cg, ref cb);
Check(Math.Abs(cr - (double)(float).1) < 1e-15 && Math.Abs(cb - (double)(float).9) < 1e-15, "Active curve flat-extends to its endpoint values.");
var turning = new FinishingTransform(new FinishingSettings { Curve = ImmutableArray.Create(new FinishingPoint(1, 1), new FinishingPoint(.5, .9), new FinishingPoint(0, 0), new FinishingPoint(.5, .2)) });
var normalizedTurning = new FinishingTransform(new FinishingSettings { Curve = ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(.5, .2), new FinishingPoint(1, 1)) });
double tr = .25, tg = .5, tb = .75, nr = tr, ng = tg, nb = tb;
turning.Apply(ref tr, ref tg, ref tb); normalizedTurning.Apply(ref nr, ref ng, ref nb);
Check(tr == nr && tg == ng && tb == nb, "Curve sorting preserves last duplicate x.");
var nearVertical = new FinishingTransform(new FinishingSettings { Curve = ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(.99, 0), new FinishingPoint(1, 1)) });
for (int i = 0; i <= 1000; i++)
{
    double r = i / 1000d, g = r, b = r;
    nearVertical.Apply(ref r, ref g, ref b);
    Check(r >= 0 && r <= 1 && r == g && r == b, "Catmull-Rom undershoot is clamped in LUT.");
}
var graySeparation = new FinishingTransform(new FinishingSettings { ColorSeparation = 1 });
double sr = .3, sg = .3, sb = .3;
graySeparation.Apply(ref sr, ref sg, ref sb);
Check(Math.Abs(sr - .3) < 1e-14 && Math.Abs(sg - .3) < 1e-14 && Math.Abs(sb - .3) < 1e-14, "Density unmixing preserves neutral.");
var tintMid = new FinishingTransform(new FinishingSettings { MidtoneStrength = 1, MidtoneHue = 120 });
double mr = 0, mg = 0, mb = 0;
tintMid.Apply(ref mr, ref mg, ref mb);
Check(mr == 0 && mg == 0 && mb == 0, "Midtone tint has zero black mask.");
mr = mg = mb = 1; tintMid.Apply(ref mr, ref mg, ref mb);
Check(mr == 1 && mg == 1 && mb == 1, "Midtone tint has zero white mask.");
mr = mg = mb = .5; tintMid.Apply(ref mr, ref mg, ref mb);
Check(mg > mr && mg > mb && Math.Abs(mr + mg + mb - 1.5) < 1e-14, "Midtone hue and zero-sum tint at peak.");
Console.WriteLine($"{count - failures.Count}/{count} assertions; {fixturePixels} RGB reference fixtures; max absolute error {maxError:R}.");
foreach (var failure in failures.Take(30)) Console.Error.WriteLine(failure);
if (args.Contains("--benchmark"))
{
    foreach (var settings in new[] { new FinishingSettings { ExposureEv = .3, Contrast = .2 }, new FinishingSettings { Saturation = 1.2, Vibrance = 1.2, HueRed = .5, ShadowStrength = .2, Curve = ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(.5, .6), new FinishingPoint(1, 1)) } })
    {
        var transform = new FinishingTransform(settings);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double accumulator = 0;
        for (int i = 0; i < 2_000_000; i++) { double r = (i % 1000) / 1000d, g = .4, b = .8; transform.Apply(ref r, ref g, ref b); accumulator += r + g + b; }
        Console.WriteLine($"Scalar 2 MP: {timer.Elapsed.TotalMilliseconds:F1} ms; checksum {accumulator:R}.");
    }
}
return failures.Count == 0 ? 0 : 1;
