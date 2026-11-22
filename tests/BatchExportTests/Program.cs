using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly panel = null!;
    static string root = "";
    static int assertions, failures;
    static Type T(string name) => panel.GetType(name, true)!;
    static object? P(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value);
    static object? Call(object value, string name, params object?[] args) =>
        (value as Type ?? value.GetType()).GetMethod(name, All)!.Invoke(value is Type ? null : value, args);
    static object New(string type, params object?[] args) => Activator.CreateInstance(T(type), All, null, args, null)!;
    static object[] List(object value) => ((IEnumerable)value).Cast<object>().ToArray();
    static void Check(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
    static void Test(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
    }
    static void Throws(Action action, string contains)
    {
        try { action(); } catch (Exception e) { Check(e.GetBaseException().Message.Contains(contains, StringComparison.OrdinalIgnoreCase), e.ToString()); return; }
        throw new Exception("Expected failure containing " + contains);
    }
    static string Folder(string name) => Path.Combine(root, name);
    static object Settings(double exposure = 0, bool enabled = true) => JsonSerializer.Deserialize(JsonSerializer.Serialize(new
    {
        Look = new { Film = "kodak_portra_400", Paper = "kodak_portra_endura", Exposure = exposure, PrintExposure = 0,
            Strength = .8, Mode = "print", SpectralMethod = "hanatos2025", ScanInvert = false,
            Development = new Dictionary<string,double> { ["film_gamma"] = 1.2 } },
        Optics = new { Numbers = new Dictionary<string,double> { ["finishingExposureEv"] = exposure },
            Selections = new Dictionary<string,string> { ["processing-engine"] = "native" },
            Switches = new Dictionary<string,bool> { ["scanner-enabled"] = true } }, Enabled = enabled, Version = 1,
        Grain = new { Type = 2, Impact = 20, Granularity = 30 }
    }), T("VariantLook"))!;
    static object Entry(string source, object? settings = null, Guid? id = null) =>
        New("BatchExportSupport+Entry", New("HostProfile+Target", id ?? Guid.NewGuid(), Path.GetFullPath(source)), settings ?? Settings(), null, null, null);
    static object Plan(string directory, string extension, params object[] entries)
    {
        var array = Array.CreateInstance(T("BatchExportSupport+Entry"), entries.Length);
        for (int i = 0; i < entries.Length; i++) array.SetValue(entries[i], i);
        return Call(T("BatchExportSupport"), "CreatePlan", array, directory, extension)!;
    }
    static async Task<object> Run(object plan, Func<object,string,CancellationToken,Task> callback, CancellationToken token = default)
    {
        var entry = Expression.Parameter(T("BatchExportSupport+Entry"));
        var path = Expression.Parameter(typeof(string)); var stop = Expression.Parameter(typeof(CancellationToken));
        var callbackType = typeof(Func<,,,>).MakeGenericType(entry.Type, typeof(string), typeof(CancellationToken), typeof(Task));
        var typed = Expression.Lambda(callbackType, Expression.Invoke(Expression.Constant(callback),
            Expression.Convert(entry, typeof(object)), path, stop), entry, path, stop).Compile();
        var task = (Task)Call(T("BatchExportSupport"), "RunAsync", plan, typed, token, null)!;
        await task.ConfigureAwait(false); return P(task, "Result")!;
    }
    static string Output(object completed) => (string)P(completed, "OutputPath")!;
    static void NoTemporary(string directory) => Check(!Directory.EnumerateFiles(directory, ".spektrafilm-*").Any(), "Temporary file leaked");

    [STAThread]
    static int Main()
    {
        root = Path.GetFullPath(Path.Combine("artifacts", "batch-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        const string c1 = @"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1, n.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1, n.Name + ".dll")) : null;
        panel = Assembly.Load("NativePanel");
        Test("Immutable per-variant snapshots and document stores", SnapshotIsolation);
        Test("Unique filenames protect sources, files, directories and duplicate variants", Names);
        Test("Sequential publication and race collisions preserve later names", () => Publish().GetAwaiter().GetResult());
        Test("Cancellation retains completed files and removes owned temporary", () => Cancel().GetAwaiter().GetResult());
        Test("Failure reports completed files and cleans only owned temporary", () => Failure().GetAwaiter().GetResult());
        Test("TIFF and PNG keep 16-bit values and embedded sRGB", () => HighDepth().GetAwaiter().GetResult());
        Test("Recipe ambiguity and missing host target rejected", Guards);
        Test("Rendering masks hidden inversion without changing persisted look", HiddenInversionMatrix);
        Console.WriteLine($"{assertions} assertions; {failures} failing groups; {root}");
        return failures == 0 ? 0 : 1;
    }
    static void SnapshotIsolation()
    {
        var original = Settings(.4); var entry = Entry(Folder("a.cr2"), original);
        ((IDictionary)P(P(original, "Look")!, "Development")!)["film_gamma"] = 9d;
        ((IDictionary)P(P(original, "Optics")!, "Numbers")!)["finishingExposureEv"] = 9d;
        var first = P(entry, "Settings")!;
        Check((double)((IDictionary)P(P(first,"Look")!,"Development")!)["film_gamma"]! == 1.2, "Development aliased");
        Check((double)((IDictionary)P(P(first,"Optics")!,"Numbers")!)["finishingExposureEv"]! == .4, "Numbers aliased");
        ((IDictionary)P(P(first, "Optics")!, "Selections")!)["processing-engine"] = "changed";
        ((IDictionary)P(P(first, "Optics")!, "Switches")!)["scanner-enabled"] = false;
        var second = P(entry, "Settings")!;
        Check((string)((IDictionary)P(P(second,"Optics")!,"Selections")!)["processing-engine"]! == "native", "Selections aliased");
        Check((bool)((IDictionary)P(P(second,"Optics")!,"Switches")!)["scanner-enabled"]!, "Switches aliased");
        var a = New("VariantLookStore", Folder("stores"), Folder("first.cosessiondb"));
        var b = New("VariantLookStore", Folder("stores"), Folder("second.cosessiondb"));
        var id = Guid.NewGuid(); Call(a, "Queue", id, Settings(.2)); Call(b, "Queue", id, Settings(-.7, false));
        Check((double)P(P(Call(a,"Read",id)!,"Look")!,"Exposure")! == .2, "Document A changed");
        Check((double)P(P(Call(b,"Read",id)!,"Look")!,"Exposure")! == -.7, "Document B changed");
        Check(!Directory.Exists(Folder("stores")), "Read snapshot unexpectedly persisted data");
    }
    static void Names()
    {
        var dir = Folder("names"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"photo-Spektrafilm.tif"), "existing");
        Directory.CreateDirectory(Path.Combine(dir,"photo-Spektrafilm-2.tif"));
        var a = Entry(Folder("src/photo.CR2")); var b = Entry(Folder("other/PHOTO.cr2"));
        var plan = Plan(dir, ".TIF", a, b);
        var names = List(P(plan,"Items")!).Select(x => Path.GetFileName((string)P(x,"OutputPath")!)).ToArray();
        Check(names.SequenceEqual(new[]{"photo-Spektrafilm-3.tif","PHOTO-Spektrafilm-4.tif"}), string.Join(",",names));
        var protectedName = Path.Combine(dir, "photo-Spektrafilm-3.tif");
        var protectedPlan = Plan(dir,".tif", a,Entry(protectedName));
        Check((string)P(List(P(protectedPlan,"Items")!)[0],"OutputPath")! != protectedName,"Source target reused");
        Throws(()=>Plan(dir,".pdf",a),"supports"); Throws(()=>Plan(dir,".tif",a,a),"duplicate");
        foreach(var extension in new[]{".jpg",".jpeg",".dng"}) {
            var existing=Path.Combine(dir,"photo-Spektrafilm"+extension);
            File.WriteAllText(existing,"preserve "+extension);
            var formatPlan=Plan(dir,extension.ToUpperInvariant(),a,b);
            var paths=List(P(formatPlan,"Items")!).Select(x=>(string)P(x,"OutputPath")!).ToArray();
            Check(paths.Select(Path.GetFileName).SequenceEqual(new[]{"photo-Spektrafilm-2"+extension,"PHOTO-Spektrafilm-3"+extension}),"Format collision: "+extension);
            Check(File.ReadAllText(existing)=="preserve "+extension,"Format overwrote existing: "+extension);
        }
        var protectedDng=Path.Combine(dir,"photo-Spektrafilm-2.dng");
        var dngPlan=Plan(dir,".dng",a,Entry(protectedDng));
        Check((string)P(List(P(dngPlan,"Items")!)[0],"OutputPath")! != protectedDng,"DNG source reserved path reused");
        Throws(()=>Plan(dir,".png"),"at least");
        var longPlan=Plan(dir,".png",Entry(Folder("src/"+new string('x',180)+".cr2")));
        Check(Path.GetFileName((string)P(List(P(longPlan,"Items")!)[0],"OutputPath")!).Length<=116,"Unbounded filename");
        Check(File.ReadAllText(Path.Combine(dir,"photo-Spektrafilm.tif"))=="existing","Existing file overwritten");
    }
    static async Task Publish()
    {
        var dir=Folder("race"); var a=Entry(Folder("a/photo.cr2"),Settings(.1));var b=Entry(Folder("b/photo.cr2"),Settings(.8));
        var plan=Plan(dir,".tif",a,b); int active=0,max=0,count=0;
        var seen=new List<double>();
        var result=await Run(plan,async(entry,temp,token)=> {
            max=Math.Max(max,Interlocked.Increment(ref active));count++;
            seen.Add((double)P(P(P(entry,"Settings")!,"Look")!,"Exposure")!);
            if(count==1) File.WriteAllText(Path.Combine(dir,"photo-Spektrafilm.tif"),"other writer");
            await Task.Delay(5,token); await File.WriteAllTextAsync(temp,"render"+count,token); Interlocked.Decrement(ref active);
        });
        var files=List(P(result,"Completed")!).Select(Output).ToArray();
        Check(max==1 && count==2,"Callbacks overlapped"); Check(seen.SequenceEqual(new[]{.1,.8}),"Wrong per-variant settings");
        Check(files.Select(Path.GetFileName).SequenceEqual(new[]{"photo-Spektrafilm-3.tif","photo-Spektrafilm-2.tif"}),"Race stole later reserved name");
        Check(File.ReadAllText(Path.Combine(dir,"photo-Spektrafilm.tif"))=="other writer","Concurrent output overwritten");
        Check(!(bool)P(result,"Cancelled")!,"Unexpected canceled result"); NoTemporary(dir);
    }
    static async Task Cancel()
    {
        var dir=Folder("cancel"); var plan=Plan(dir,".png",Entry(Folder("a.cr2")),Entry(Folder("b.cr2")),Entry(Folder("c.cr2")));
        using var stop=new CancellationTokenSource(); int count=0;
        var result=await Run(plan,(_,temp,_)=> {File.WriteAllText(temp,"render");if(++count==2)stop.Cancel();return Task.CompletedTask;},stop.Token);
        Check((bool)P(result,"Cancelled")! && List(P(result,"Completed")!).Length==1 && count==2,"Cancellation lost boundary");
        Check(Directory.EnumerateFiles(dir).Count()==1,"Canceled file published"); NoTemporary(dir);
        var beforeDir=Folder("cancel-before");var before=await Run(Plan(beforeDir,".tif",Entry(Folder("x.cr2"))),(_,_,_)=>throw new Exception("Called after cancel"),stop.Token);
        Check((bool)P(before,"Cancelled")! && !Directory.Exists(beforeDir),"Pre-canceled created directory");
    }
    static async Task Failure()
    {
        var dir=Folder("failure");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"unrelated.tmp"),"keep");int count=0;
        try {
            await Run(Plan(dir,".tif",Entry(Folder("a.cr2")),Entry(Folder("b.cr2"))),(_,temp,_)=> {
                File.WriteAllText(temp,"partial");if(++count==2)throw new InvalidDataException("synthetic decode failure");return Task.CompletedTask;
            });throw new Exception("Missing failure");
        } catch(Exception e) when(e.GetType().Name=="BatchExportException") {
            Check(List(P(e,"Completed")!).Length==1,"Completed files omitted from error");
            Check(e.InnerException is InvalidDataException,"Original failure hidden");
        }
        Check(File.ReadAllText(Path.Combine(dir,"unrelated.tmp"))=="keep","Unrelated file deleted");NoTemporary(dir);
        var emptyDir=Folder("empty");
        try {await Run(Plan(emptyDir,".tif",Entry(Folder("a.cr2"))),(_,_,_)=>Task.CompletedTask);throw new Exception("Empty output accepted");}
        catch(Exception e) when(e.GetType().Name=="BatchExportException") {Check(e.Message.Contains("did not produce"),"Wrong empty-file error");}
        NoTemporary(emptyDir);
    }
    static async Task HighDepth()
    {
        foreach(var extension in new[]{".tif",".png"}) {
            var format=extension==".tif"?PixelFormats.Rgb48:PixelFormats.Rgba64;int channels=format.BitsPerPixel/16;
            ushort[] pixels=Enumerable.Range(0,16*channels).Select(i=>(ushort)(300+i*997)).ToArray();
            var bitmap=BitmapSource.Create(4,4,96,96,format,null,pixels,4*channels*2);bitmap.Freeze();
            var result=await Run(Plan(Folder("depth"),extension,Entry(Folder("depth.cr2"))),(_,temp,token)=> {
                Call(T("SpektraExport"),"Save",temp,bitmap,token);return Task.CompletedTask;
            });
            var file=Output(List(P(result,"Completed")!)[0]);
            using var stream=File.OpenRead(file);var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.Format==format,"Lost high-depth format: "+frame.Format);Check(frame.ColorContexts?.Count>0,"ICC missing");
            var actual=new ushort[pixels.Length];frame.CopyPixels(actual,4*channels*2,0);
            Check(actual.SequenceEqual(pixels),"16-bit values changed");NoTemporary(Path.GetDirectoryName(file)!);
        }
    }
    static void Guards()
    {
        var type=T("BatchExportSupport");Call(type,"ValidateSingleRecipe",1,"Export","Export");assertions++;
        foreach(var count in new[]{0,2}) Throws(()=>Call(type,"ValidateSingleRecipe",count,"Export","Export"),"one Export Recipe");
        Throws(()=>Call(type,"ValidateSingleRecipe",1,"A","B"),"one Export Recipe");
        var entry=Entry(Folder("a.cr2"));
        Throws(()=>Call(entry,"CaptureSource",new object(),Enum.ToObject(T("PreviewInputSpace"),0),false,Enum.ToObject(T("SourcePreparation"),0),null),"no captured");
    }
    static void HiddenInversionMatrix()
    {
        var saved=P(Settings(.4),"Look")!;
        saved.GetType().GetProperty("ScanInvert")!.SetValue(saved,true);
        var original=JsonSerializer.Serialize(saved,saved.GetType());
        foreach(var kind in new[]{"negative","positive"})foreach(var mode in new[]{"scan","print"}) {
            saved.GetType().GetProperty("Mode")!.SetValue(saved,mode);
            var catalog=JsonSerializer.Deserialize(JsonSerializer.Serialize(new {
                Films=new[]{new{Id="kodak_portra_400",Label="Film",Kind=kind}},Papers=Array.Empty<object>(),Modes=Array.Empty<object>(),SpectralMethods=Array.Empty<object>()
            }),T("LookOptions"))!;
            var rendered=Call(T("BatchExportSupport"),"RenderingLook",saved,catalog)!;
            Check((bool)P(rendered,"ScanInvert")! == (kind=="negative" && mode=="scan"),"Hidden inversion escaped to renderer");
            Check((bool)P(saved,"ScanInvert")!,"Stored inversion erased");
            Check((double)P(rendered,"Exposure")! == .4,"Unrelated look changed");
        }
        saved.GetType().GetProperty("Mode")!.SetValue(saved,"print");
        Check(JsonSerializer.Serialize(saved,saved.GetType())==original,"Saved look mutated");
        var empty=JsonSerializer.Deserialize("{\"Films\":[],\"Papers\":[],\"Modes\":[],\"SpectralMethods\":[]}",T("LookOptions"))!;
        Throws(()=>Call(T("BatchExportSupport"),"RenderingLook",saved,empty),"unavailable film");
    }
}
