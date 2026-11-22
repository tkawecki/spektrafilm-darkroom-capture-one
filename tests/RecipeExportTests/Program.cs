using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

// Recipe export logic that needs no Capture One: plan collisions and original protection, the render space chosen
// for a recipe profile, the look description in XMP, the repair of byte-swapped 16-bit PNG, the export journal and
// publishing finished files. Exercised through reflection on the production panel assembly.
internal static class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly panel = null!;
    static string root = "";
    static int assertions, failures;
    static Type T(string name) => panel.GetType(name, true)!;
    static object? P(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value);
    static object? Call(string type, string name, params object?[] args) =>
        T(type).GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args);
    static object New(string type, params object?[] args) => Activator.CreateInstance(T(type), All, null, args, null)!;
    static object E(string type, string value) => Enum.Parse(T(type), value);
    static object[] List(object value) => ((IEnumerable)value).Cast<object>().ToArray();
    static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    static void Test(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e.GetBaseException()); }
    }
    static void Throws(Action action, string contains)
    {
        try { action(); }
        catch (Exception e) { Check(e.GetBaseException().Message.Contains(contains, StringComparison.OrdinalIgnoreCase), e.GetBaseException().Message); return; }
        throw new Exception("Expected failure containing " + contains);
    }
    static string Folder(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }

    // Example folders under the temporary folder: no machine-specific drive paths in the source.
    static string O(string name) => Path.Combine(Path.GetTempPath(), "sf-recipe-out", name);
    static string Ph(string name) => Path.Combine(Path.GetTempPath(), "sf-recipe-photos", name);
    static object Request(string desired, string collision = "Rename", string? unsupported = null, string? source = null, string recipe = "R") =>
        New("PlanRequest", Guid.NewGuid(), source ?? Ph("a.CR2"), recipe, desired, E("CollisionAction", collision), unsupported);

    static object[] Resolve(IEnumerable<object> requests, IEnumerable<string> originals, Func<string, bool> exists)
    {
        var typed = Array.CreateInstance(T("PlanRequest"), requests.Count());
        var i = 0; foreach (var r in requests) typed.SetValue(r, i++);
        return List(Call("RecipeExportPlanning", "Resolve", typed, originals, exists, null)!);
    }
    static string Outcome(object file) => P(file, "Outcome")!.ToString()!;
    static string Output(object file) => (string)P(file, "OutputPath")!;

    [STAThread]
    static int Main()
    {
        root = Path.GetFullPath(Path.Combine("artifacts", "recipe-export-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var c1 = Environment.GetEnvironmentVariable("CAPTURE_ONE_DIRECTORY") ?? @"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
        panel = Assembly.Load("NativePanel");

        Test("Free names follow Capture One: name, name_1, name_2", () =>
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { O("a.jpg"), O("a_1.jpg") };
            Check((string)Call("RecipeExportPlanning", "FreeName", O("b.jpg"), (Func<string, bool>)taken.Contains)! == O("b.jpg"), "free name changed");
            Check((string)Call("RecipeExportPlanning", "FreeName", O("a.jpg"), (Func<string, bool>)taken.Contains)! == O("a_2.jpg"), "wrong next name");
        });
        Test("Recipe overwrite actions map to collisions", () =>
        {
            Check(Call("RecipeExportPlanning", "CollisionFrom", "Overwrite")!.ToString() == "Overwrite", "overwrite");
            Check(Call("RecipeExportPlanning", "CollisionFrom", "Skip")!.ToString() == "Skip", "skip");
            Check(Call("RecipeExportPlanning", "CollisionFrom", "Rename")!.ToString() == "Rename", "rename");
            Check(Call("RecipeExportPlanning", "CollisionFrom", (string?)null)!.ToString() == "Rename", "unknown is rename");
        });
        Test("Existing files: rename, replace or skip as the recipe says", () =>
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { O("a.jpg") };
            var files = Resolve(new[] { Request(O("a.jpg")), Request(O("a.jpg"), "Overwrite"), Request(O("a.jpg"), "Skip"), Request(O("b.jpg")) },
                Array.Empty<string>(), existing.Contains);
            Check(Outcome(files[0]) == "Renamed" && Output(files[0]) == O("a_1.jpg"), "rename");
            // The first output took a_1.jpg; the second recipe replaces the file that existed before the export.
            Check(Outcome(files[1]) == "Replaces" && Output(files[1]) == O("a.jpg"), "overwrite of a pre-existing file");
            Check(Outcome(files[2]) == "Skipped", "skip");
            Check(Outcome(files[3]) == "New" && Output(files[3]) == O("b.jpg"), "new");
            var replace = Resolve(new[] { Request(O("a.jpg"), "Overwrite") }, Array.Empty<string>(), existing.Contains);
            Check(Outcome(replace[0]) == "Replaces" && Output(replace[0]) == O("a.jpg"), "replace");
        });
        Test("Outputs of one export never share a name", () =>
        {
            var files = Resolve(new[] { Request(O("x.tif")), Request(O("X.TIF")), Request(O("x.tif")) }, Array.Empty<string>(), _ => false);
            var outputs = files.Select(Output).ToArray();
            Check(outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, string.Join(", ", outputs));
            Check(Outcome(files[1]) == "Renamed" && P(files[1], "Note") is string, "duplicate needs a note");
        });
        Test("An original is never replaced, even with Overwrite", () =>
        {
            var files = Resolve(new[] { Request(Ph("a.CR2"), "Overwrite") }, new[] { Ph("a.CR2") }, path => path == Ph("a.CR2"));
            Check(Outcome(files[0]) == "Renamed" && Output(files[0]) != Ph("a.CR2"), "original replaced");
            var other = Resolve(new[] { Request(Ph("a_1.CR2")) }, new[] { Ph("a.CR2") }, _ => false);
            Check(Outcome(other[0]) == "New", "unrelated name renamed");
        });
        Test("Overwrite never replaces files in the folder of the originals", () =>
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Ph("a.jpg"), O("a.jpg") };
            var files = Resolve(new[] { Request(Ph("a.jpg"), "Overwrite"), Request(O("a.jpg"), "Overwrite") },
                new[] { Ph("a.CR2") }, existing.Contains);
            Check(Outcome(files[0]) == "Renamed" && Output(files[0]) == Ph("a_1.jpg"), "file next to the original replaced");
            Check(Outcome(files[1]) == "Replaces", "replace elsewhere");
        });
        Test("Unsupported recipe formats are listed, not written", () =>
        {
            var files = Resolve(new[] { Request(O("a.CR2"), unsupported: "the recipe format Original is not a processed image") }, Array.Empty<string>(), _ => false);
            Check(Outcome(files[0]) == "NotSupported" && !(bool)P(files[0], "Writes")!, "unsupported written");
        });
        Test("Render space: sRGB and Adobe RGB pass through, everything else via ProPhoto", () =>
        {
            string Space(string? name, string? path = null) => Call("RecipeExportPlanning", "RenderSpaceForProfile", name, path)!.ToString()!;
            Check(Space("sRGB Color Space Profile.icm") == "Srgb", "srgb");
            Check(Space(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm")) == "Srgb", "srgb path");
            Check(Space("Adobe RGB (1998).icm") == "AdobeRgb", "adobe");
            Check(Space("ProPhoto.icm") == "ProPhotoRgb", "prophoto");
            Check(Space("Epson SC-P900 Premium Luster.icc") == "ProPhotoRgb", "printer");
            Check(Space("") == "ProPhotoRgb", "empty");
        });
        Test("Look description is added to Capture One's XMP and stays valid XML", () =>
        {
            var look = New("RecipeExportPlanning+LookDescription", "Kodak Portra 400", "Endura <glossy> & co", "Print Simulation", 0.5, -0.25, 0.8, "Version \"A\"", "Spektrafilm 1.2");
            const string c1Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\" tiff:Model=\"EOS\"/></rdf:RDF></x:xmpmeta>";
            var result = (string)Call("RecipeExportPlanning", "AddLook", c1Xmp, look)!;
            var xml = XDocument.Parse(result);
            XNamespace sf = (string)T("RecipeExportPlanning").GetField("XmpNamespace", All)!.GetValue(null)!;
            Check(xml.Descendants(sf + "FilmStock").Single().Value == "Kodak Portra 400", "film");
            Check(xml.Descendants(sf + "PrintStock").Single().Value == "Endura <glossy> & co", "escaping");
            Check(xml.Descendants(sf + "FilmExposure").Single().Value == "0.5" && xml.Descendants(sf + "PrintExposure").Single().Value == "-0.25", "invariant numbers");
            Check(result.Contains("tiff:Model=\"EOS\""), "Capture One's metadata lost");
            var empty = XDocument.Parse((string)Call("RecipeExportPlanning", "AddLook", "", look)!);
            Check(empty.Descendants(sf + "LookStrength").Single().Value == "0.8", "minimal packet");
        });

        // A 16-bit RGB PNG written by WPF (standard byte order) with a text chunk, and its swapped copy as Capture One writes it.
        byte[] StandardPng(out ushort[] samples, int width = 37, int height = 11)
        {
            samples = new ushort[width * height * 3];
            for (var i = 0; i < samples.Length; i++) samples[i] = (ushort)(i * 977 % 65536);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb48, null, samples, width * 6);
            var encoder = new PngBitmapEncoder();
            var metadata = new BitmapMetadata("png");
            metadata.SetQuery("/tEXt/{str=Description}", "Spektrafilm test");
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        ushort[] DecodeWithWpf(byte[] png)
        {
            var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            BitmapSource source = frame.Format == PixelFormats.Rgb48 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Rgb48, null, 0);
            var result = new ushort[frame.PixelWidth * frame.PixelHeight * 3];
            source.CopyPixels(result, frame.PixelWidth * 6, 0);
            return result;
        }
        List<string> ChunkTypes(byte[] png)
        {
            var types = new List<string>();
            for (var o = 8; o + 12 <= png.Length;)
            {
                var length = png[o] << 24 | png[o + 1] << 16 | png[o + 2] << 8 | png[o + 3];
                types.Add(System.Text.Encoding.ASCII.GetString(png, o + 4, 4));
                o += 12 + length;
            }
            return types;
        }
        Test("16-bit PNG: swapped samples are detected and repaired, other chunks kept", () =>
        {
            var standard = StandardPng(out var samples);
            var swapped = Swap(standard);
            var wrong = DecodeWithWpf(swapped);
            Check(!wrong.SequenceEqual(samples), "swap had no effect");
            var rows = new Dictionary<int, ushort[]>();
            foreach (var row in new[] { 2, 5, 9 }) rows[row] = samples.AsSpan(row * 37 * 3, 37 * 3).ToArray();
            Check(IsSwapped(swapped, rows), "swapped file not detected");
            Check(!IsSwapped(standard, rows), "standard file reported as swapped");
            var repaired = Swap(swapped);
            Check(DecodeWithWpf(repaired).SequenceEqual(samples), "repaired samples differ");
            Check(ChunkTypes(repaired).Contains("tEXt") && ChunkTypes(repaired).Count(t => t == "IDAT") == 1, string.Join(",", ChunkTypes(repaired)));
        });
        Test("PNG decoding handles every row filter", () =>
        {
            // WPF chooses filters adaptively; a noisy image exercises Sub, Up, Average and Paeth.
            var width = 64; var height = 48; var samples = new ushort[width * height * 3]; var random = new Random(7);
            for (var i = 0; i < samples.Length; i++) samples[i] = (ushort)((i % (width * 3) * 300 + random.Next(0, 2000)) & 0xFFFF);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb48, null, samples, width * 6);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var png = stream.ToArray();
            // Swapping twice must give back the same samples, through every filter WPF chose and the encoder's own.
            Check(DecodeWithWpf(Swap(Swap(png))).SequenceEqual(samples), "unfiltered samples differ");
        });
        Test("PNG repair streams a large image in several IDAT chunks", () =>
        {
            var width = 1500; var height = 900; var samples = new ushort[width * height * 3]; var random = new Random(3);
            for (var i = 0; i < samples.Length; i++) samples[i] = (ushort)random.Next(0, 65536);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb48, null, samples, width * 6);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var swapped = Swap(stream.ToArray());
            Check(ChunkTypes(swapped).Count(t => t == "IDAT") > 1, "expected several IDAT chunks");
            Check(DecodeWithWpf(Swap(swapped)).SequenceEqual(samples), "large image differs after two swaps");
        });
        Test("Journal: saved, read back, finished or foreign journals ignored", () =>
        {
            var folder = Folder("journal");
            var catalog = Path.Combine(root, "Test.cosessiondb");
            var items = Array.CreateInstance(T("JournalItem"), 2);
            items.SetValue(New("JournalItem", Guid.NewGuid(), Ph("a.CR2"), "R1", O("a.jpg"), E("PlannedOutcome", "New"), E("JournalState", "Done"), null, null), 0);
            var temporary = Path.Combine(Folder("out"), ".spektrafilm-0123.jpg");
            File.WriteAllText(temporary, "partial");
            var keep = Path.Combine(Folder("out"), "keep.jpg");
            File.WriteAllText(keep, "user file");
            items.SetValue(New("JournalItem", Guid.NewGuid(), Ph("b.CR2"), "R1", O("b.jpg"), E("PlannedOutcome", "Renamed"), E("JournalState", "Pending"), temporary, null), 1);
            var list = Activator.CreateInstance(typeof(ReadOnlyCollection<>).MakeGenericType(T("JournalItem")), Activator.CreateInstance(typeof(List<>).MakeGenericType(T("JournalItem")), items));
            var journal = New("ExportJournal", 1, Guid.NewGuid(), catalog, DateTime.UtcNow, list);
            Call("RecipeExportJournal", "Save", journal, folder);
            var loaded = Call("RecipeExportJournal", "Load", catalog, folder)!;
            Check((int)P(loaded, "RemainingCount")! == 1, "remaining");
            Check(Call("RecipeExportJournal", "Load", Path.Combine(root, "Other.cosessiondb"), folder) is null, "foreign catalog read");
            Call("RecipeExportJournal", "RemoveLeftovers", loaded);
            Check(!File.Exists(temporary) && File.Exists(keep), "leftover cleanup removed the wrong files");
            var done = T("ExportJournal").GetMethod("With", All)!.Invoke(loaded, new object[] { 1,
                New("JournalItem", Guid.NewGuid(), Ph("b.CR2"), "R1", O("b.jpg"), E("PlannedOutcome", "Renamed"), E("JournalState", "Done"), null, null) })!;
            Call("RecipeExportJournal", "Save", done, folder);
            Check(Call("RecipeExportJournal", "Load", catalog, folder) is null, "finished journal offered for resume");
            Call("RecipeExportJournal", "Delete", catalog, folder);
            Check(!Directory.EnumerateFiles(folder).Any(), "journal not deleted");
        });
        Test("Publishing: rename when taken, replace only when planned, never an original", () =>
        {
            var folder = Folder("publish");
            var original = Path.Combine(folder, "photo.tif");
            File.WriteAllText(original, "original");
            File.WriteAllText(Path.Combine(folder, "taken.jpg"), "existing");
            object Planned(string output, string outcome) => New("PlannedFile", Guid.NewGuid(), original, "R", output, output, E("PlannedOutcome", outcome), null);
            string Publish(object file, HashSet<string> claimed)
            {
                var temporary = Path.Combine(folder, ".spektrafilm-" + Guid.NewGuid().ToString("N") + ".jpg");
                File.WriteAllText(temporary, "render");
                return (string)T("SpatialPreview").GetMethod("PublishRecipeFile", All)!.Invoke(null, new object[] { temporary, file, new[] { original }, claimed })!;
            }
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renamed = Publish(Planned(Path.Combine(folder, "taken.jpg"), "New"), claimed);
            Check(Path.GetFileName(renamed) == "taken_1.jpg" && File.ReadAllText(Path.Combine(folder, "taken.jpg")) == "existing", "taken file replaced");
            var replaced = Publish(Planned(Path.Combine(folder, "taken.jpg"), "Replaces"), claimed);
            Check(replaced == Path.Combine(folder, "taken.jpg") && File.ReadAllText(replaced) == "render", "planned replace not done");
            var again = Publish(Planned(Path.Combine(folder, "taken.jpg"), "Replaces"), claimed);
            Check(Path.GetFileName(again) == "taken_2.jpg", "an output of this export was replaced: " + again);
            Throws(() => Publish(Planned(original, "Replaces"), claimed), "original");
            Check(File.ReadAllText(original) == "original", "original changed");
        });

        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        Console.WriteLine($"{assertions} assertions, {failures} failed tests");
        return failures == 0 ? 0 : 1;
    }

    static byte[] Swap(byte[] png)
    {
        using var source = new MemoryStream(png);
        using var target = new MemoryStream();
        panel.GetType("RecipeExportPng", true)!.GetMethod("SwapSamples", All)!.Invoke(null, new object[] { source, target });
        return target.ToArray();
    }

    static bool IsSwapped(byte[] png, Dictionary<int, ushort[]> rows) =>
        (bool)panel.GetType("RecipeExportPng", true)!.GetMethod("IsSwapped", All)!.Invoke(null, new object[] { new MemoryStream(png), rows })!;
}
