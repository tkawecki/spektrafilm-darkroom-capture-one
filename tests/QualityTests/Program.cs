using System;
using System.IO;
using System.Linq;
using Expression = System.Linq.Expressions.Expression;
using System.Collections.Generic;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
partial class Program {
static Assembly panel = null!;
static int failures;
[STAThread] static int Main(string[] args) {
var c1 = @"C:\Program Files\Capture One\Capture One";
AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(c1,n.Name+".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")) : null;
panel = Assembly.Load("NativePanel");
if(args.Length==3 && args[0]=="--dump-reference") {
 Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT",Path.GetFullPath(args[1]));
 var current=Look();var view=View(()=>current);
 try {CallView(view,"SelectEngine",true);var options=(string[])CallView(view,"RendererOptions",current,"prophoto","srgb",true,false)!;
 File.WriteAllText(args[2],JsonSerializer.Serialize(options,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Reference arguments: "+options.Length/2);return 0;}
 finally {((IDisposable)view).Dispose();}
}
Test("Viewer downsampling suppresses aliasing, preserves 100% pixels", Scaling);
Test("Neutral preparation is nondestructive for global/local corrections", Preparation);
Test("New defaults and sparse saved looks use separate baselines", Defaults);
Test("Developer exhaustion reaches Print renderer and remains inactive in Scan", Exhaustion);
Test("Mode-specific optical controls retain values while inactive", Capabilities);
Test("Finishing controls persist neutral defaults and stay out of Vulkan arguments", FinishingControls);
if(args.Length == 2) Test("All saved document/variant looks roundtrip on copies",()=>SavedLooks(args[0],args[1]));
Console.WriteLine($"Failures: {failures}"); return failures==0?0:1;
}
static void Test(string name, Action test) { try {test(); Console.WriteLine("PASS "+name);} catch(Exception e) {failures++; Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);} }
static void Check(bool value,string message) { if(!value) throw new Exception(message); }
internal static object Invoke(string type,string method,params object?[] args) => panel.GetType(type,true)!.GetMethod(method,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,args)!;
static void FinishingControls() {
var look=panel.GetType("LookSettings",true)!;
var callback=Expression.Lambda(typeof(Func<>).MakeGenericType(look),Expression.Constant(null,look)).Compile();
var view=Invoke("SpatialPreview","BuildForPanel",new object(),callback,new Func<Guid,bool>(_=>true));
try {
var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var settings=view.GetType().GetMethod("CaptureSettings",flags)!.Invoke(view,null)!;
var data=JsonSerializer.SerializeToElement(settings);
Check(data.GetProperty("Numbers").GetProperty("finishingExposureEv").GetDouble()==0,"Finishing exposure default");
Check(data.GetProperty("Numbers").GetProperty("finishingSaturation").GetDouble()==1,"Finishing saturation default");
Check(data.GetProperty("Switches").GetProperty("finishing-enabled").GetBoolean(),"Finishing enabled default");
var captured=view.GetType().GetMethod("CaptureFinishingSettings",flags)!.Invoke(view,null)!;
var transform=Activator.CreateInstance(panel.GetType("FinishingTransform",true)!,new[]{captured})!;
Check((bool)transform.GetType().GetProperty("IsIdentity")!.GetValue(transform)!,"Neutral finishing changes pixels");
Check(view.GetType().GetMethod("DecodeExportWithFinishing",BindingFlags.Static|BindingFlags.NonPublic)!=null,"Export integration absent");
var values=(System.Collections.IDictionary)view.GetType().GetField("values",flags)!.GetValue(view)!;
var curve=values["finishingCurveMidtones"]!;
curve.GetType().GetProperty("Value",flags)!.SetValue(curve,.6d);
var first=view.GetType().GetMethod("CaptureFinishingSettings",flags)!.Invoke(view,null);
var second=view.GetType().GetMethod("CaptureFinishingSettings",flags)!.Invoke(view,null);
Check(ReferenceEquals(first,second),"Unchanged active curve missed immutable snapshot cache");
} finally { ((IDisposable)view).Dispose(); }
}
static byte[] Render(BitmapSource bitmap, int width, bool quality, double dpi=96) {
var visual=new DrawingVisual(); using(var dc=visual.RenderOpen()) {
dc.PushTransform(new ScaleTransform((double)width/bitmap.PixelWidth*96/dpi,96/dpi));
if(quality && panel.GetType("PreviewDrawing") is not null) dc.DrawDrawing((Drawing)Invoke("PreviewDrawing","Create",bitmap));
else dc.DrawImage(bitmap,new Rect(0,0,bitmap.PixelWidth,bitmap.PixelHeight));
dc.Pop(); }
var output=new RenderTargetBitmap(width,1,dpi,dpi,PixelFormats.Pbgra32); output.Render(visual);
var bytes=new byte[width*4]; output.CopyPixels(bytes,width*4,0); return bytes;
}
static void Scaling() {
var pixels=Enumerable.Range(0,1024).SelectMany(i=>new byte[]{(byte)(i%2*255),(byte)(i%2*255),(byte)(i%2*255),255}).ToArray();
var image=BitmapSource.Create(1024,1,96,96,PixelFormats.Bgra32,null,pixels,4096); image.Freeze();
var reduced=Render(image,137,true);
var errors=Enumerable.Range(2,133).Select(i=>Math.Abs(reduced[i*4]-127.5)).ToArray();
var baseline=Render(image,137,false);
var oldError=Enumerable.Range(2,133).Average(i=>Math.Abs(baseline[i*4]-127.5));
Check(errors.Average()<16 && errors.Average()<oldError/4,"Aliased checkerboard mean error="+errors.Average());
foreach(var dpi in new[]{96d,120d,144d,192d}) Check(Render(image,1024,true,dpi).SequenceEqual(pixels),"100% pixels changed at DPI "+dpi);
}
static void Preparation() {
InputPreparationChecks.Run();
var a=Assembly.Load("P1.C1.ImageCoreWrapper"); var t=a.GetType("ImgCore.IC_ProcessSettings",true)!;
var s=Activator.CreateInstance(t)!;
var image=t.GetField("imageProcessSettings")!.FieldType;
var img=Activator.CreateInstance(image)!;
image.GetField("USMAmount")!.SetValue(img,140f); image.GetField("exposure")!.SetValue(img,.75f);
t.GetField("imageProcessSettings")!.SetValue(s,img);
var curve=t.GetField("filmCurve")!.FieldType;
t.GetField("filmCurve")!.SetValue(s,curve.GetMethod("Create",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,new object[]{Path.Combine(@"C:\Program Files\Capture One\Capture One\Film Curves","CanonEOS5DSR-Auto.fcrv")}));
var prepared=Invoke("InputPreparation","Prepare",s);
Check((float)image.GetField("USMAmount")!.GetValue(t.GetField("imageProcessSettings")!.GetValue(prepared))! == 0,"Sharpening remains");
Check((float)image.GetField("USMAmount")!.GetValue(t.GetField("imageProcessSettings")!.GetValue(s))! == 140,"Original changed");
Check((float)image.GetField("exposure")!.GetValue(t.GetField("imageProcessSettings")!.GetValue(prepared))! == .75f,"Exposure changed");
Check(t.GetField("filmCurve")!.GetValue(prepared)!.ToString()!.EndsWith("CanonEOS5DSR-Linear Response.fcrv"),"Wrong curve");
}
static void SavedLooks(string input, string output) {
var lookType=panel.GetType("VariantLook",true)!; var storeType=panel.GetType("VariantLookStore",true)!;
var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
int count=0;
foreach(var file in Directory.GetFiles(input,"*.json",SearchOption.AllDirectories)) {
 var payload=JsonSerializer.Deserialize(File.ReadAllText(file),lookType)!;
 var document=Path.Combine(Path.GetFullPath(output),Path.GetFileName(Path.GetDirectoryName(file))!+".cosessiondb");
 var store=Activator.CreateInstance(storeType,flags,null,new object[]{Path.GetFullPath(output),document},null)!;
 var id=Guid.ParseExact(Path.GetFileNameWithoutExtension(file),"N");
 storeType.GetMethod("Queue",flags)!.Invoke(store,new[]{(object)id,payload});
 storeType.GetMethod("Flush",flags)!.Invoke(store,null);
 var loaded=storeType.GetMethod("Read",flags)!.Invoke(store,new object[]{id});
 Check(JsonSerializer.Serialize(payload)==JsonSerializer.Serialize(loaded),"Saved look altered: "+file); count++;
}
Check(count>0,"No saved looks tested"); Console.WriteLine("Copied variant looks verified: "+count);
}
static void Defaults() {
var look=panel.GetType("LookSettings",true)!;
var callbackType=typeof(Func<>).MakeGenericType(look);
var callback=Expression.Lambda(callbackType,Expression.Constant(null,look)).Compile();
var view=Invoke("SpatialPreview","BuildForPanel",new object(),callback,new Func<Guid,bool>(_=>true));
var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
var type=view.GetType();
object Capture()=>type.GetMethod("CaptureSettings",flags)!.Invoke(view,null)!;
void Load(object? settings)=>type.GetMethod("LoadSettings",flags)!.Invoke(view,new[]{settings});
JsonElement Data()=>JsonSerializer.SerializeToElement(Capture());
try {
var fresh=Data();
var controls=(System.Collections.IDictionary)type.GetField("values",flags)!.GetValue(view)!;
var choices=(System.Collections.IDictionary)type.GetField("choices",flags)!.GetValue(view)!;
foreach(var stage in new[]{"camera","print"}) {
 var filter=(System.Windows.Controls.ComboBox)choices[stage+"-diffusion-family"]!;
 var strength=(FrameworkElement)controls[stage+"-diffusion-strength"]!;
 Check(!strength.IsEnabled,stage+" diffusion controls stay active while filter is Off");
 filter.SelectedIndex=1;
 Check(strength.IsEnabled,stage+" diffusion cannot be adjusted after choosing a filter");
 strength.GetType().GetProperty("Value",flags)!.SetValue(strength,1.375);
 filter.SelectedIndex=0;
 Check(!strength.IsEnabled,stage+" diffusion controls remain active after switching Off");
 Check((double)strength.GetType().GetProperty("Value",flags)!.GetValue(strength)! == 1.375,stage+" diffusion strength lost while Off");
 var retained=Capture(); Load(null); Load(retained);
 Check(!strength.IsEnabled && (double)strength.GetType().GetProperty("Value",flags)!.GetValue(strength)! == 1.375,stage+" stored Off state or strength lost");
 filter.SelectedIndex=1;
 Check(strength.IsEnabled,stage+" restored filter cannot be enabled");
 Load(null);
}
Check(fresh.GetProperty("Numbers").GetProperty("dir-diffusion").GetDouble()==0,"New spatial DIR active");
Check(fresh.GetProperty("Switches").GetProperty("color-adaptation").GetBoolean(),"New gamut protection off");
Check(fresh.GetProperty("Switches").GetProperty("auto-exposure").GetBoolean(),"New neutral input needs exposure metering");
Check(fresh.GetProperty("Selections").GetProperty("input-color").GetString()=="prophoto","New input gamut too narrow");
Check(fresh.GetProperty("Selections").GetProperty("input-preparation").GetString()=="neutral-film","New preparation inactive");
var settingsType=panel.GetType("SpatialPreview+Settings",true)!;
var sparse=Activator.CreateInstance(settingsType,new object[]{new Dictionary<string,double>{{"halation-amount",1.35}}, new Dictionary<string,string>(),new Dictionary<string,bool>()})!;
Load(sparse); var legacy=Data();
Check(legacy.GetProperty("Numbers").GetProperty("dir-diffusion").GetDouble()==20,"Sparse legacy DIR changed");
Check(!legacy.GetProperty("Switches").GetProperty("color-adaptation").GetBoolean(),"Sparse legacy gamut changed");
Check(!legacy.GetProperty("Switches").GetProperty("auto-exposure").GetBoolean(),"Sparse legacy exposure changed");
Check(legacy.GetProperty("Numbers").GetProperty("halation-amount").GetDouble()==1.35,"Saved number changed");
Check(legacy.GetProperty("Selections").GetProperty("input-color").GetString()=="srgb","Legacy input changed");
Check(legacy.GetProperty("Selections").GetProperty("input-preparation").GetString()=="capture-one","Legacy preparation changed");
var roundtrip=Capture(); Load(null); Load(roundtrip);
Check(Data().ToString()==JsonSerializer.SerializeToElement(roundtrip).ToString(),"Roundtrip settings changed");
Load(null); Check(Data().ToString()==fresh.ToString(),"Defaults reset failed");
} finally {((IDisposable)view).Dispose();}
}
}
