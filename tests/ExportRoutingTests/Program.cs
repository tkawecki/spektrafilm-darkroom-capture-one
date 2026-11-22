using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using P1.C1;
using P1.C1.Components;
using P1.C1.Components.CommandsLogic;
using P1.C1.Components.ExportDialog;
using P1.C1.Core.Interfaces;

internal static class Tests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int assertions, failures;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); }
    }
    [STAThread]
    private static int Main()
    {
        foreach (var route in new[] { "dialog", "previous", "confirm" })
        {
            Test(route + ": multiple selection is intercepted and retains editMultiple=true", () => Route(route, true, true, true));
            Test(route + ": disabled primary with enabled secondary intercepts selected variants", () => Route(route, true, false, true));
            Test(route + ": primary-only active exports only primary despite multiple selection", () => Route(route, false, true, true));
            Test(route + ": disabled primary-only falls through to native even with enabled secondary", () => Route(route, false, false, true));
            Test(route + ": all disabled falls through unchanged", () => Route(route, true, false, false));
        }
        Test("Cancelled export returns false, preserves mode and clears busy", () => Completion(false));
        Test("Successful export returns true, preserves mode and clears busy", () => Completion(true));
        Test("An in-flight export suppresses duplicate commands and resets after cancellation", () => {
            using var f = new Fixture(true, true) { Pending = new TaskCompletionSource<bool>() };
            f.Manager.Commands.ExportWithPreviousSettings.Execute(true);
            Check((bool)typeof(ExportHook).GetField("busy", Hidden)!.GetValue(f.Hook)!, "Pending export did not set busy");
            f.Manager.Commands.ExportWithPreviousSettings.Execute(false);
            f.Manager.Commands.Export.Execute(true);
            Check(f.Exported.SequenceEqual(new[] { true }) && ExportDialogFactory.Opened.Count == 0, "Duplicate command reached renderer/dialog");
            f.Pending.SetResult(false);
            Check(!(bool)typeof(ExportHook).GetField("busy", Hidden)!.GetValue(f.Hook)!, "Cancelled export left busy set");
        });
        Test("Recipe restriction still blocks before rendering", () => {
            using var f = new Fixture(true, true);
            f.Manager.EnabledDestinations.Add(new Destination { Name = "Second" });
            f.Manager.Commands.ExportWithPreviousSettings.Execute(true);
            Check(f.Exported.Count == 0 && System.Windows.Forms.MessageBox.Warnings.Count == 1, "Multiple recipes unexpectedly rendered");
        });
        Test("Recipe export lets every active recipe reach the Spektrafilm export", () => {
            using var f = new Fixture(true, true) { SingleRecipeRequired = false };
            f.Manager.EnabledDestinations.Add(new Destination { Name = "Second" });
            f.Manager.Commands.ExportWithPreviousSettings.Execute(true);
            Check(f.Exported.SequenceEqual(new[] { true }) && System.Windows.Forms.MessageBox.Warnings.Count == 0, "Several recipes were blocked");
        });
        Test("Wrong document falls through to native without evaluating look state", () => {
            using var f = new Fixture(true, true);
            f.Manager.ActiveDocument = new TestDocument();
            f.Manager.Commands.Export.Execute(false);
            f.Manager.Commands.ExportWithPreviousSettings.Execute(true);
            Check(f.ActiveChecks.Count == 0 && f.Exported.Count == 0, "Stale document evaluated/routed a look");
            Check(f.Native.NativeDialog.SequenceEqual(new[] { false }) && f.Native.NativePrevious.SequenceEqual(new[] { true }), "Native mode changed for a stale document");
        });
        Test("Empty selection does not call renderer", () => {
            using var f = new Fixture(true, true);
            f.Document.PrimarySelectedCollectionSelection!.SelectedVariants!.Clear();
            f.Manager.Commands.ExportWithPreviousSettings.Execute(true);
            Check(f.Exported.Count == 0 && System.Windows.Forms.MessageBox.Warnings.Count == 1, "Empty selection reached export");
        });
        Test("Disposal restores both native callbacks", () => {
            var f = new Fixture(true, true); f.Hook.Dispose();
            f.Manager.Commands.Export.Execute(true); f.Manager.Commands.ExportWithPreviousSettings.Execute(false);
            Check(f.Native.NativeDialog.SequenceEqual(new[] { true }) && f.Native.NativePrevious.SequenceEqual(new[] { false }), "Native callback restoration failed");
            f.Dispose();
        });
        Console.WriteLine($"{assertions} assertions; {failures} failing groups.");
        return failures == 0 ? 0 : 1;
    }
    private static void Route(string route, bool multiple, bool primary, bool secondary)
    {
        using var f = new Fixture(primary, secondary);
        var expectedActive = primary || multiple && secondary;
        int nativeConfirm = 0;
        if (route == "dialog") f.Manager.Commands.Export.Execute(multiple);
        else if (route == "previous") f.Manager.Commands.ExportWithPreviousSettings.Execute(multiple);
        else ((Task<bool>)typeof(ExportHook).GetMethod("Confirm", Hidden)!.Invoke(f.Hook,
            new object[] { new Func<Task<bool>>(() => { nativeConfirm++; return Task.FromResult(true); }), multiple })!).GetAwaiter().GetResult();
        var nativeCount = route == "dialog" ? f.Native.NativeDialog.Count : route == "previous" ? f.Native.NativePrevious.Count : nativeConfirm;
        Check(nativeCount == (expectedActive ? 0 : 1), "Wrong native/Spektrafilm route");
        Check(f.ActiveChecks.SequenceEqual(new[] { multiple }), "isActive lost the requested primary/selected mode");
        Check(System.Windows.Forms.MessageBox.Warnings.Count == 0, "Valid selection produced a warning");
        var actual = route == "dialog" ? ExportDialogFactory.Opened : f.Exported;
        Check(actual.SequenceEqual(expectedActive ? new[] { multiple } : Array.Empty<bool>()), "Intercepted export lost mode or rejected a valid batch");
    }
    private static void Completion(bool result)
    {
        using var f = new Fixture(true, true) { ExportResult = result };
        var method = typeof(ExportHook).GetMethod("ExportAsync", Hidden)!;
        var actual = ((Task<bool>)method.Invoke(f.Hook, method.GetParameters().Length == 0 ? null : new object[] { false })!).GetAwaiter().GetResult();
        Check(actual == result, "Export result changed");
        Check(f.Exported.SequenceEqual(new[] { false }), "Primary-only export flag was lost");
        Check(!(bool)typeof(ExportHook).GetField("busy", Hidden)!.GetValue(f.Hook)!, "Busy state remained after completion");
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly TestDocument Document = new() { PrimarySelectedCollectionSelection = new Selection { PrimaryVariant = new object(), SelectedVariants = new() { new(), new() } } };
        internal readonly TestManager Manager;
        internal readonly TestForm Form;
        internal readonly CommandHookerFile Native;
        internal readonly ExportHook Hook;
        internal readonly List<bool> Exported = new(), ActiveChecks = new();
        internal bool ExportResult = true;
        internal bool SingleRecipeRequired = true;
        internal TaskCompletionSource<bool>? Pending;
        internal Fixture(bool primary, bool secondary)
        {
            ExportDialogFactory.Opened.Clear(); System.Windows.Forms.MessageBox.Warnings.Clear();
            Manager = new TestManager { ActiveDocument = Document }; P1.C1.Program.DocumentManager = Manager;
            Form = new TestForm { Document = Document }; Native = new CommandHookerFile(Form);
            Manager.Commands.Export.OnExecuteMultiple = Native.OnExportOnExecuteMultiple;
            Manager.Commands.ExportWithPreviousSettings.OnExecuteMultiple = Native.OnExportWithPreviousOnExecuteMultiple;
            bool Active(bool multiple) { ActiveChecks.Add(multiple); return primary || multiple && secondary; }
            Task<bool> Export(bool multiple) { Exported.Add(multiple); return Pending?.Task ?? Task.FromResult(ExportResult); }
            // Supports the old callback shape only to expose its behavior in the initial failing run.
            var constructor = typeof(ExportHook).GetConstructors(Hidden).Single();
            object active = constructor.GetParameters()[1].ParameterType == typeof(Func<bool>)
                ? new Func<bool>(() => Active(true)) : new Func<bool, bool>(Active);
            object export = constructor.GetParameters()[2].ParameterType == typeof(Func<Task<bool>>)
                ? new Func<Task<bool>>(() => Export(true)) : new Func<bool, Task<bool>>(Export);
            var arguments = new List<object?> { Form, active, export };
            if (constructor.GetParameters().Length > 3) arguments.Add(new Func<bool>(() => SingleRecipeRequired));
            Hook = (ExportHook)constructor.Invoke(arguments.ToArray());
        }
        public void Dispose() { Hook.Dispose(); Form.Dispose(); }
    }
}
