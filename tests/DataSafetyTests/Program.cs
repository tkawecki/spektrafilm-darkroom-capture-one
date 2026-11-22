// Data-safety ports from nothing-complex 285f0aa: look preservation/recovery, Capture One Grain rollback,
// output-path aliasing and renderer environment sanitizing. Runs without Capture One; uses a scratch folder.
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;

const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
const string c1 = @"C:\Program Files\Capture One\Capture One";
AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
var panel = Assembly.Load("NativePanel");
Type T(string s) => panel.GetType(s, true)!;
object? Call(object o, string m, params object?[] a)
{
    try { return (o as Type ?? o.GetType()).GetMethod(m, all)!.Invoke(o is Type ? null : o, a); }
    catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
}
object? P(object o, string n) => o.GetType().GetProperty(n, all)!.GetValue(o);
var assertions = 0;
void Check(bool ok, string why) { assertions++; if (!ok) throw new Exception(why); }
bool Throws<TError>(Action action) where TError : Exception { try { action(); return false; } catch (TError) { return true; } }

object Look(double ev) => JsonSerializer.Deserialize(JsonSerializer.Serialize(new
{
    Look = new { Film = "kodak_portra_400", Paper = "kodak_portra_endura", Exposure = ev, PrintExposure = 0, Strength = 1, Mode = "print", SpectralMethod = "hanatos2025", ScanInvert = false, Development = new Dictionary<string, double>() },
    Optics = new { Numbers = new Dictionary<string, double> { { "halation-amount", ev } }, Selections = new Dictionary<string, string> { { "processing-engine", "native" } }, Switches = new Dictionary<string, bool>() },
    Enabled = true, Version = 1,
}), T("VariantLook"))!;
double Exposure(object? look) => (double)P(P(look!, "Look")!, "Exposure")!;

var scratch = Path.GetFullPath(Path.Combine("artifacts", "data-safety-tests", Guid.NewGuid().ToString("N")));
var root = Path.Combine(scratch, "Variant looks");
Directory.CreateDirectory(root);
var catalog = Path.Combine(scratch, "Test.cosessiondb");
object Store() => Activator.CreateInstance(T("VariantLookStore"), all, null, new object[] { root, catalog }, null)!;
string Folder(object store) => Path.GetDirectoryName(Path.GetDirectoryName((string)Call(store, "VersionsFolder", Guid.Empty)!)!)!;
string Preserved(object store) => Path.Combine(scratch, "Look recovery", Path.GetFileName(Folder(store)), "Preserved originals");
string[] PreservedFiles(object store, Guid variant) => Directory.Exists(Preserved(store))
    ? Directory.GetFiles(Preserved(store), variant.ToString("N") + "-*") : Array.Empty<string>();

// 1. Normal save and read keep the existing file format.
{
    var store = Store(); var variant = Guid.NewGuid();
    Call(store, "Queue", variant, Look(1.5)); Call(store, "Flush");
    Check(Exposure(Call(Store(), "Read", variant)) == 1.5, "saved look not read back");
    Check(PreservedFiles(store, variant).Length == 0, "valid look must not be archived");
}
// 2. An unreadable look is copied aside before Defaults/preset replaces it.
{
    var store = Store(); var variant = Guid.NewGuid();
    var path = Path.Combine(Folder(store), variant.ToString("N") + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{ broken json");
    Check(Throws<JsonException>(() => Call(store, "Read", variant)), "corrupt look must not load");
    Call(store, "Queue", variant, Look(0)); Call(store, "Flush");
    var copies = PreservedFiles(store, variant);
    Check(copies.Length == 1 && File.ReadAllText(copies[0]) == "{ broken json", "unreadable look not preserved byte-exact");
    Check(Exposure(Call(Store(), "Read", variant)) == 0, "replacement not saved");
}
// 3. A readable look that cannot be applied (missing stock/dependency) is also preserved.
{
    var store = Store(); var variant = Guid.NewGuid();
    Call(store, "Queue", variant, Look(2)); Call(store, "Flush");
    var fresh = Store(); Call(fresh, "Read", variant); Call(fresh, "ProtectUnloadable", variant);
    Call(fresh, "Queue", variant, Look(0)); Call(fresh, "Flush");
    var copies = PreservedFiles(fresh, variant);
    Check(copies.Length == 1 && Exposure(JsonSerializer.Deserialize(File.ReadAllText(copies[0]), T("VariantLook"))) == 2, "unloadable look not preserved");
}
// 4. A failed save leaves a recovery checkpoint that is restored after a restart.
{
    var store = Store(); var variant = Guid.NewGuid();
    var path = Path.Combine(Folder(store), variant.ToString("N") + ".json");
    Directory.CreateDirectory(path); // A directory in place of the file makes the atomic move fail.
    Call(store, "Queue", variant, Look(3.25));
    Check(Throws<IOException>(() => Call(store, "Flush")), "failed save must be reported");
    var recovery = Path.Combine(scratch, "Look recovery", Path.GetFileName(Folder(store)), variant.ToString("N") + ".pending.json");
    Check(File.Exists(recovery), "recovery checkpoint missing");
    Directory.Delete(path);
    var restarted = Store();
    Check(Exposure(Call(restarted, "Read", variant)) == 3.25, "recovered edit not offered");
    Check(P(restarted, "RecoveryNotice") is string, "recovery not announced");
    Call(restarted, "Flush");
    Check(!File.Exists(recovery) && Exposure(Call(Store(), "Read", variant)) == 3.25, "recovered edit not saved or checkpoint kept");
}
// 5. A checkpoint older than the saved look never overrides it; it is kept aside instead.
{
    var store = Store(); var variant = Guid.NewGuid();
    var recovery = Path.Combine(scratch, "Look recovery", Path.GetFileName(Folder(store)), variant.ToString("N") + ".pending.json");
    Call(store, "Queue", variant, Look(4)); Call(store, "Flush");
    Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
    File.WriteAllText(recovery, JsonSerializer.Serialize(Look(-1)));
    File.SetLastWriteTimeUtc(recovery, DateTime.UtcNow.AddHours(-1));
    var restarted = Store();
    Check(Exposure(Call(restarted, "Read", variant)) == 4 && P(restarted, "RecoveryNotice") is null, "stale checkpoint replaced newer look");
    Check(!File.Exists(recovery) && PreservedFiles(restarted, variant).Length == 1, "stale checkpoint not archived");
}
// 5b. Members written by a newer panel survive a save by this (older) panel; known values are replaced
//     and a reset finishing curve (a key omitted on purpose) is not brought back.
{
    var store = Store(); var variant = Guid.NewGuid();
    var path = Path.Combine(Folder(store), variant.ToString("N") + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, """
    { "Look": { "Film": "kodak_portra_400", "Paper": "kodak_portra_endura", "Exposure": 1, "PrintExposure": 0, "Strength": 1,
                "Mode": "print", "SpectralMethod": "hanatos2025", "ScanInvert": false, "Development": { "future_dev": 0.5 },
                "FutureLookField": "x" },
      "Optics": { "Numbers": { "halation-amount": 1, "future-number": 7 },
                  "Selections": { "processing-engine": "native", "future-selection": "keep", "finishing-curve-red-points": "0,0;1,1" },
                  "Switches": { "future-switch": true }, "FutureOptics": [1, 2] },
      "Enabled": true, "Version": 1, "FutureTopLevel": { "a": 1 } }
    """);
    System.Text.Json.Nodes.JsonObject Saved() => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    void CheckKept(System.Text.Json.Nodes.JsonObject json, double ev, string when)
    {
        Check((double)json["Look"]!["Exposure"]! == ev && (double)json["Optics"]!["Numbers"]!["halation-amount"]! == ev, "known values not replaced " + when);
        Check((int)json["FutureTopLevel"]!["a"]! == 1 && (string)json["Look"]!["FutureLookField"]! == "x", "unknown top-level/look member lost " + when);
        Check((double)json["Look"]!["Development"]!["future_dev"]! == .5 && (double)json["Optics"]!["Numbers"]!["future-number"]! == 7, "unknown development/number lost " + when);
        Check((string)json["Optics"]!["Selections"]!["future-selection"]! == "keep" && (bool)json["Optics"]!["Switches"]!["future-switch"]!, "unknown selection/switch lost " + when);
        Check(json["Optics"]!["FutureOptics"]!.AsArray().Count == 2, "unknown optics member lost " + when);
        Check(!json["Optics"]!["Selections"]!.AsObject().ContainsKey("finishing-curve-red-points"), "reset curve brought back " + when);
    }
    Check(Exposure(Call(store, "Read", variant)) == 1, "look with newer members not readable");
    Call(store, "Queue", variant, Look(2.5)); Call(store, "Flush");
    CheckKept(Saved(), 2.5, "after the first save");
    var again = Store(); Call(again, "Read", variant); Call(again, "Queue", variant, Look(3)); Call(again, "Flush");
    CheckKept(Saved(), 3, "after a second save");
    var unread = Store(); Call(unread, "Queue", variant, Look(4)); Call(unread, "Flush"); // e.g. a batch apply
    CheckKept(Saved(), 4, "when saved without an earlier read");
    Check(PreservedFiles(unread, variant).Length == 0, "readable look with newer members must not be archived");
}
// 5c. A look made with the removed second engine (readable, but refused) can be repaired: after Defaults
//     the new file carries neither the engine marker nor the old members, and the original is archived.
{
    var store = Store(); var variant = Guid.NewGuid();
    var path = Path.Combine(Folder(store), variant.ToString("N") + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, """
    { "Look": { "Film": "kodak_portra_400", "Paper": "kodak_portra_endura", "Exposure": 1, "PrintExposure": 0, "Strength": 1,
                "Mode": "print", "SpectralMethod": "hanatos2025", "ScanInvert": false, "Development": {} },
      "Optics": { "Numbers": {}, "Selections": { "processing-engine": "dyeworks-2.2.3", "legacy-preset": "large" }, "Switches": { "quick-drag-preview": false } },
      "Enabled": true, "Version": 1 }
    """);
    Call(store, "Read", variant); Call(store, "ProtectUnloadable", variant);
    Call(store, "Queue", variant, Look(0)); Call(store, "Flush");
    var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
    Check((string?)json["Optics"]!["Selections"]!["processing-engine"] == "native", "repaired look must use the new value, not the removed engine marker");
    Check(!json["Optics"]!["Selections"]!.AsObject().ContainsKey("legacy-preset"), "unloadable look must not pass its old members on");
    Check(PreservedFiles(store, variant).Length == 1, "refused look not archived before repair");
    // Also when the engine key is simply absent from the new look, it is not brought back.
    var plain = Store(); var other = Guid.NewGuid();
    var otherPath = Path.Combine(Folder(plain), other.ToString("N") + ".json");
    File.WriteAllText(otherPath, File.ReadAllText(path).Replace("\"native\"", "\"dyeworks-2.2.3\""));
    var noEngine = JsonSerializer.Deserialize(JsonSerializer.Serialize(Look(0)).Replace("\"processing-engine\":\"native\"", "\"other\":\"x\""), T("VariantLook"))!;
    Call(plain, "Read", other); Call(plain, "Queue", other, noEngine); Call(plain, "Flush");
    var otherJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(otherPath))!;
    Check(!otherJson["Optics"]!["Selections"]!.AsObject().ContainsKey("processing-engine"), "removed engine marker brought back by the merge");
    Check(!otherJson["Optics"]!["Switches"]!.AsObject().ContainsKey("quick-drag-preview"), "retired drag switch brought back by the merge");
}
// 6. Capture One Grain: a setter that throws after mutating is rolled back with earlier steps.
{
    var stepType = T("NativeAdjustmentTransaction+Step");
    var values = new Dictionary<string, int> { { "type", 0 }, { "amount", 0 } };
    var steps = Array.CreateInstance(stepType, 2);
    steps.SetValue(Activator.CreateInstance(stepType, "type", (Action)(() => values["type"] = 3), (Action)(() => values["type"] = 0)), 0);
    steps.SetValue(Activator.CreateInstance(stepType, "amount", (Action)(() => { values["amount"] = 50; throw new InvalidOperationException("setter failed"); }), (Action)(() => values["amount"] = 0)), 1);
    var scopeEnded = false;
    Check(Throws<InvalidOperationException>(() => Call(T("NativeAdjustmentTransaction"), "Execute", steps, (Action)(() => scopeEnded = true))), "setter failure not reported");
    Check(values["type"] == 0 && values["amount"] == 0 && scopeEnded, "partial grain change not rolled back");
}
// 7. Output path guard: the same file under another spelling or through a junction is refused.
{
    var images = Path.Combine(scratch, "images"); Directory.CreateDirectory(images);
    var original = Path.Combine(images, "photo.tif"); File.WriteAllText(original, "original");
    var guard = T("OutputPathGuard");
    Check(Throws<IOException>(() => Call(guard, "Validate", original.ToUpperInvariant(), original)), "case alias accepted");
    var junction = Path.Combine(scratch, "alias");
    using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{images}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        mklink.WaitForExit();
    Check(Directory.Exists(junction), "test junction not created");
    Check(Throws<IOException>(() => Call(guard, "Validate", Path.Combine(junction, "photo.tif"), original)), "junction alias accepted");
    Check((string)Call(guard, "Validate", Path.Combine(junction, "photo-Spektrafilm.tif"), original)! == Path.Combine(junction, "photo-Spektrafilm.tif"), "new output refused");
    Directory.Delete(junction);
    Check(File.ReadAllText(original) == "original", "original changed");
}
// 8. Worker environment: pass-disabling variables are removed, hardware/cache/tiling choices kept.
{
    var start = new ProcessStartInfo("cmd.exe");
    start.Environment["SPEKTRAFILM_VULKAN_HALATION_PASS"] = "0";
    start.Environment["SPEKTRAFILM_BLUR_DOWNSAMPLE"] = "4";
    start.Environment["SPEKTRAFILM_GPU"] = "cpu";
    start.Environment["DOTNET_STARTUP_HOOKS"] = "x.dll";
    start.Environment["SPEKTRAFILM_REFERENCE_CACHE"] = "reference-cache";
    start.Environment["SPEKTRAFILM_VULKAN_DEVICE_INDEX"] = "1";
    start.Environment["PATH_TEST_KEEP"] = "1";
    Call(T("WorkerEnvironment"), "Sanitize", start);
    foreach (var removed in new[] { "SPEKTRAFILM_VULKAN_HALATION_PASS", "SPEKTRAFILM_BLUR_DOWNSAMPLE", "SPEKTRAFILM_GPU", "DOTNET_STARTUP_HOOKS" })
        Check(!start.Environment.ContainsKey(removed), removed + " leaked to the renderer");
    foreach (var kept in new[] { "SPEKTRAFILM_REFERENCE_CACHE", "SPEKTRAFILM_VULKAN_DEVICE_INDEX", "PATH_TEST_KEEP" })
        Check(start.Environment.ContainsKey(kept), kept + " removed");
}
Directory.Delete(scratch, recursive: true);
Console.WriteLine($"PASS {assertions} data-safety assertions: preserved/recovered looks, grain rollback, path aliases, worker environment.");
