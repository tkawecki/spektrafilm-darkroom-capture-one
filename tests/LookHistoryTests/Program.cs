using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Undo history of Spektrafilm looks (LookHistory, 2026-11-19): coalescing of rapid changes, discrete steps,
// redo truncation, per-variant isolation, the step limit, Settle after re-applying, and that a serialized
// VariantLook survives a round trip unchanged (snapshots must compare equal after undo).
const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
const string c1 = @"C:\Program Files\Capture One\Capture One";
AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
var panel = Assembly.Load("NativePanel");
var historyType = panel.GetType("LookHistory", true)!;
object? Call(object o, string m, params object?[] a) => o.GetType().GetMethod(m, all)!.Invoke(o, a);
object NewHistory(int limit = 200) => Activator.CreateInstance(historyType, all, null, new object[] { TimeSpan.FromMilliseconds(800), limit }, null)!;

var assertions = 0;
void Check(bool ok, string why) { assertions++; if (!ok) throw new Exception(why); }
var t0 = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
DateTime At(int ms) => t0.AddMilliseconds(ms);
var a = Guid.NewGuid();
var b = Guid.NewGuid();

// Seed, coalesced drag, pause, discrete action.
var h = NewHistory();
Check((bool)Call(h, "Record", a, "S0", At(0), false)!, "Seed not recorded");
Check(!(bool)Call(h, "CanUndo", a)!, "Seed alone can be undone");
Check((bool)Call(h, "Record", a, "S1", At(100), false)!, "First edit not recorded");
Call(h, "Record", a, "S2", At(300), false);
Call(h, "Record", a, "S3", At(600), false);
Check((string?)Call(h, "Undo", a) == "S0", "A rapid drag was not one step (seed must survive the first edit)");
Check((string?)Call(h, "Redo", a) == "S3", "Redo did not return the end of the drag");
Check(!(bool)Call(h, "Record", a, "S3", At(700), false)!, "An unchanged look became a step");
Call(h, "Record", a, "S4", At(2000), false);
Call(h, "Record", a, "D", At(2100), true);
Call(h, "Record", a, "S5", At(2200), false);
Check((string?)Call(h, "Undo", a) == "D", "A change right after a discrete action merged into it");
Check((string?)Call(h, "Undo", a) == "S4", "A discrete action merged into the preceding change");
Check((string?)Call(h, "Undo", a) == "S3", "A change after a pause merged into the previous step");
Check((string?)Call(h, "Undo", a) == "S0", "Undo order wrong");
Check(Call(h, "Undo", a) is null && !(bool)Call(h, "CanUndo", a)!, "Undo went past the oldest look");
Check((bool)Call(h, "CanRedo", a)!, "Redo lost after undo");

// A new change after undo truncates redo, and does not overwrite the look it returned to.
Call(h, "Redo", a);
Check((string?)Call(h, "Undo", a) == "S0", "Redo then undo did not return");
Call(h, "Record", a, "N1", At(2300), false);
Check(!(bool)Call(h, "CanRedo", a)!, "Redo survived a new change");
Check((string?)Call(h, "Undo", a) == "S0", "A change right after undo overwrote the restored look");

// Settle replaces the current step (normalised re-apply) and keeps redo.
Call(h, "Settle", a, "S0n");
Check((bool)Call(h, "CanRedo", a)!, "Settle cleared redo");
Check(!(bool)Call(h, "Record", a, "S0n", At(5000), false)!, "The settled look became a new step");
Check((string?)Call(h, "Redo", a) == "N1", "Settle broke redo");

// Variants are independent.
Call(h, "Record", b, "B0", At(0), false);
Check(!(bool)Call(h, "CanUndo", b)!, "Variant B inherited A's history");
Check(Call(h, "Undo", Guid.NewGuid()) is null, "An unknown variant could be undone");
Call(h, "Record", b, "B1", At(1000), false);
Check((string?)Call(h, "Undo", b) == "B0" && (string?)Call(h, "Redo", a) is null, "Undo on B changed A");

// Step limit keeps the newest steps.
var limited = NewHistory(3);
for (var i = 0; i < 6; ++i) Call(limited, "Record", a, "L" + i, At(i * 1000), false);
Check((string?)Call(limited, "Undo", a) == "L4" && (string?)Call(limited, "Undo", a) == "L3", "Limit dropped recent steps");
Check(Call(limited, "Undo", a) is null, "Limit kept too many steps");

// Peek does not move; Commit does (the panel commits only after the look was applied).
var peek = NewHistory();
Call(peek, "Record", a, "P0", At(0), false);
Call(peek, "Record", a, "P1", At(1000), false);
Check((string?)Call(peek, "Peek", a, false) == "P0" && (string?)Call(peek, "Current", a) == "P1", "Peek moved the history");
Check(Call(peek, "Peek", a, true) is null, "Peek found a redo step that does not exist");
Call(peek, "Commit", a, false);
Check((string?)Call(peek, "Current", a) == "P0" && (bool)Call(peek, "CanRedo", a)!, "Commit did not move back");

// A clock that steps backwards does not keep merging changes into one step.
var clock = NewHistory();
Call(clock, "Record", a, "C0", At(5000), false);
Call(clock, "Record", a, "C1", At(6000), false);
Call(clock, "Record", a, "C2", At(4000), false);
Check((string?)Call(clock, "Undo", a) == "C1", "A backward clock merged a later change into the previous step");

// Invalid input.
var threw = false;
try { Call(h, "Record", Guid.Empty, "X", At(0), false); } catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { threw = true; }
Check(threw, "An empty variant was accepted");
threw = false;
try { Activator.CreateInstance(historyType, all, null, new object[] { TimeSpan.Zero, 1 }, null); } catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { threw = true; }
Check(threw, "A history without room for a step back was accepted");

// A full VariantLook snapshot is stable through deserialization (what undo applies and Settle compares).
var lookType = panel.GetType("VariantLook", true)!;
var look = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
{
    Look = new { Film = "kodak_portra_400", Paper = "kodak_portra_endura", Exposure = -.4, PrintExposure = .3, Strength = .6, Mode = "print",
        SpectralMethod = "hanatos2025", ScanInvert = false, Development = new Dictionary<string, double> { { "print_gamma", .85 }, { "preflash_exposure", .05 } } },
    Optics = new { Numbers = new Dictionary<string, double> { { "halation-amount", .5 } }, Selections = new Dictionary<string, string> { { "grain-model", "production" } },
        Switches = new Dictionary<string, bool> { { "auto-exposure", true } } },
    Enabled = true, Version = 1, Grain = new { Type = 2, Impact = 25d, Granularity = 50d },
}), lookType)!;
var snapshot = JsonSerializer.Serialize(look, lookType);
Check(JsonSerializer.Serialize(JsonSerializer.Deserialize(snapshot, lookType), lookType) == snapshot, "A look snapshot changed through a round trip");

Console.WriteLine($"PASS {assertions} undo history assertions: coalescing, discrete steps, redo, Settle, variants, limit, snapshot round trip");
