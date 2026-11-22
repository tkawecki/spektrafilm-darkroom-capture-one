// Option wiring audit: every panel control (slider, choice, toggle) is changed on a detached
// SpatialPreview and the resulting renderer CLI arguments are compared with the baseline, in
// Print and Scan. Writes a JSON report; fails on a control that is enabled but changes nothing.
// Usage: OptionWiringTests <out.json>   (run from the repository root; no C1 window, no renderer)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using Expression = System.Linq.Expressions.Expression;

internal static class Program
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    static int Main(string[] args)
    {
        var scratch = Path.GetFullPath(Path.Combine("artifacts", "option-wiring", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        // The staged runtime (stocks.json, renderer/, profiles/): SPEKTRAFILM_TEST_RUNTIME, else a local test copy, else the repository root after Build-Windows.ps1 -StageRuntime.
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_TEST_RUNTIME") is { Length: > 0 } staged ? staged : Directory.Exists("artifacts/no-dw/runtime") ? "artifacts/no-dw/runtime" : "."));
        Environment.SetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG", Path.Combine(scratch, "panel.log"));
        const string c1 = @"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
        var assembly = Assembly.Load("NativePanel");
        var type = assembly.GetType("SpatialPreview", true)!; var lookType = assembly.GetType("LookSettings", true)!;
        object Look(string mode) => JsonSerializer.Deserialize("{\"Film\":\"kodak_portra_400\",\"Paper\":\"kodak_portra_endura\",\"Mode\":\"" + mode +
            "\",\"SpectralMethod\":\"hanatos2025\",\"Exposure\":0.4,\"PrintExposure\":0,\"Strength\":1,\"Development\":{}}", lookType)!;
        var holder = new StrongBox<object>(Look("print"));
        var callback = Expression.Lambda(typeof(Func<>).MakeGenericType(lookType),
            Expression.Convert(Expression.Field(Expression.Constant(holder), "Value"), lookType)).Compile();
        var view = type.GetMethod("BuildForPanel", Flags)!.Invoke(null, new object[] { new object(), callback, new Func<Guid, bool>(_ => true) })!;
        object? Call(string method, params object?[] a) => type.GetMethod(method, Flags)!.Invoke(view, a);
        IDictionary Table(string name) => (IDictionary)type.GetField(name, Flags)!.GetValue(view)!;
        Dictionary<string, string> Args()
        {
            var flat = (string[])Call("RendererOptions", holder.Value, "prophoto", "srgb", true, false)!;
            var map = new Dictionary<string, string>();
            for (var i = 0; i < flat.Length; i += 2) map[flat[i]] = flat[i + 1];
            return map;
        }
        string[] Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
            after.Keys.Union(before.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k)).OrderBy(k => k).ToArray();
        bool Enabled(object control) => ((FrameworkElement)control).IsEnabled && IsVisibleChain((FrameworkElement)control);
        static bool IsVisibleChain(FrameworkElement element)
        {
            for (DependencyObject? e = element; e is not null; e = System.Windows.LogicalTreeHelper.GetParent(e))
                if (e is UIElement u && !u.IsEnabled) return false;
            return true;
        }

        var report = new List<object>(); var failures = new List<string>();
        foreach (var mode in new[] { "print", "scan" })
        {
            holder.Value = Look(mode);
            Call("LoadSettings", new object?[] { null }); // Native defaults for new looks
            Call("UpdateCapabilities");
            if (args.Length > 1) File.WriteAllText(Path.Combine(args[1], "defaults-" + mode + ".json"), JsonSerializer.Serialize(Args()));
            if (args.Length > 1 && mode == "print")
            {
                var ranges = new Dictionary<string, object>();
                foreach (DictionaryEntry e in Table("values"))
                {
                    var c = e.Value!; var t = c.GetType();
                    ranges[(string)e.Key] = new { min = t.GetProperty("Minimum", Flags)!.GetValue(c), max = t.GetProperty("Maximum", Flags)!.GetValue(c), value = t.GetProperty("Value", Flags)!.GetValue(c) };
                }
                foreach (DictionaryEntry e in Table("choices"))
                    ranges[(string)e.Key] = new { choices = ((System.Windows.Controls.ComboBox)e.Value!).ItemsSource.Cast<object>().Select(x => (string)x.GetType().GetProperty("Id")!.GetValue(x)!).ToArray() };
                foreach (DictionaryEntry e in Table("toggles")) ranges[(string)e.Key] = new { toggle = ((System.Windows.Controls.CheckBox)e.Value!).IsChecked == true };
                File.WriteAllText(Path.Combine(args[1], "ranges.json"), JsonSerializer.Serialize(ranges, new JsonSerializerOptions { WriteIndented = true }));
            }
            // Activate the models whose sub-controls would otherwise be legitimately inactive.
            var enablers = new List<(string Table, string Key, object Value)>
            {
                ("values","halation-amount",1.0), ("values","scatter-amount",0.5), ("values","halation-boost",2.0),
                ("toggles","scanner-enabled",true), ("toggles","scanner-white",true), ("toggles","scanner-black",true),
                ("toggles","custom-halation-rgb",true), ("toggles","custom-dir-rgb",true), ("toggles","film-aging-enabled",true),
            };
            void Apply(string table, string key, object value)
            {
                var control = Table(table)[key]!;
                if (table == "values") control.GetType().GetProperty("Value", Flags)!.SetValue(control, Convert.ToDouble(value));
                else if (table == "toggles") ((System.Windows.Controls.CheckBox)control).IsChecked = (bool)value;
                else { var box = (System.Windows.Controls.ComboBox)control; box.SelectedItem = box.ItemsSource.Cast<object>().First(x => (string)x.GetType().GetProperty("Id")!.GetValue(x)! == (string)value); }
            }
            foreach (var (t, k, v) in enablers) Apply(t, k, v);
            foreach (var grain in new[] { "production", "synthesis" })
            {
                Apply("choices", "grain-model", grain);
                foreach (var family in new[] { "black_pro_mist" }) { Apply("choices", "camera-diffusion-family", family); if (mode == "print") Apply("choices", "print-diffusion-family", family); }
                Call("UpdateCapabilities");
                foreach (var table in new[] { "values", "toggles", "choices" })
                {
                    foreach (DictionaryEntry entry in Table(table))
                    {
                        var key = (string)entry.Key; var control = entry.Value!;
                        if (table == "choices" && key == "grain-model") continue;
                        var baseline = Args(); var enabled = Enabled(control);
                        var tried = new List<object>();
                        IEnumerable<object> Alternatives()
                        {
                            if (table == "values")
                            {
                                var p = control.GetType();
                                double min = (double)p.GetProperty("Minimum", Flags)!.GetValue(control)!, max = (double)p.GetProperty("Maximum", Flags)!.GetValue(control)!;
                                double cur = (double)p.GetProperty("Value", Flags)!.GetValue(control)!;
                                yield return cur + (max - cur > cur - min ? (max - cur) * 0.5 : -(cur - min) * 0.5);
                            }
                            else if (table == "toggles") yield return ((System.Windows.Controls.CheckBox)control).IsChecked != true;
                            else foreach (var item in ((System.Windows.Controls.ComboBox)control).ItemsSource.Cast<object>())
                                {
                                    var id = (string)item.GetType().GetProperty("Id")!.GetValue(item)!;
                                    if (item != ((System.Windows.Controls.ComboBox)control).SelectedItem) yield return id;
                                }
                        }
                        object? original = table switch
                        {
                            "values" => control.GetType().GetProperty("Value", Flags)!.GetValue(control),
                            "toggles" => ((System.Windows.Controls.CheckBox)control).IsChecked == true,
                            _ => (string)((System.Windows.Controls.ComboBox)control).SelectedItem.GetType().GetProperty("Id")!.GetValue(((System.Windows.Controls.ComboBox)control).SelectedItem)!,
                        };
                        foreach (var alt in Alternatives())
                        {
                            Apply(table, key, alt);
                            var changed = Changed(baseline, Args());
                            tried.Add(new { value = alt, changed });
                            Apply(table, key, original!);
                        }
                        var any = tried.Any(x => ((string[])x.GetType().GetProperty("changed")!.GetValue(x)!).Length > 0);
                        var localOnly = key.StartsWith("finishing", StringComparison.Ordinal) || key.StartsWith("export-", StringComparison.Ordinal);
                        if (enabled && !any && !localOnly) failures.Add($"{mode}/{grain}: enabled {table}:{key} changes no renderer argument");
                        report.Add(new { mode, grain, table, key, enabled, localOnly, tried });
                    }
                }
            }
        }
        File.WriteAllText(args.Length > 0 ? args[0] : Path.Combine(scratch, "wiring.json"), JsonSerializer.Serialize(new { failures, report }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var f in failures.Distinct()) Console.WriteLine("FAIL " + f);
        Console.WriteLine($"{report.Count} control checks; {failures.Distinct().Count()} enabled controls without effect on renderer arguments");
        Directory.Delete(scratch, true);
        return failures.Count == 0 ? 0 : 1;
    }
}
