using System.Collections;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program {
const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
static Assembly a=null!;
static Type T(string name)=>a.GetType(name,true)!;
static object? Call(object target,string name,params object?[] args)=>(target is Type t?t:target.GetType()).GetMethod(name,All)!.Invoke(target is Type?null:target,args);
static void Check(bool ok,string why) {if(!ok)throw new Exception(why);}
[STAThread] static int Main(string[] args) {
try {
const string c1=@"C:\Program Files\Capture One\Capture One";
AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
a=Assembly.Load("NativePanel");
Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT",Path.GetFullPath(args[0]));
foreach(var (mode,strength,finished) in new[]{("print",1d,false),("print",.63,true),("scan",1d,true),("print",0d,true)}) Run(mode,strength,finished);
return 0;
} catch(Exception e) {Console.WriteLine(e);return 1;}
}
static void Run(string mode,double strength,bool finished) {
var look=JsonSerializer.Deserialize(JsonSerializer.Serialize(new{
Film="kodak_portra_400",Paper="kodak_portra_endura",Exposure=.2,PrintExposure=.1,Strength=strength,
Mode=mode,SpectralMethod="hanatos2025",ScanInvert=mode=="scan",Development=new Dictionary<string,double>{{"print_exhaustion",.35}},FilmPushPullMode="standard"}),T("LookSettings"))!;
var callback=Expression.Lambda(typeof(Func<>).MakeGenericType(T("LookSettings")),Expression.Constant(look,T("LookSettings"))).Compile();
using var view=(IDisposable)Call(T("SpatialPreview"),"BuildForPanel",new object(),callback,new Func<Guid,bool>(_=>true))!;
if(finished){var values=(IDictionary)view.GetType().GetField("values",All)!.GetValue(view)!;
 foreach(var (key,value) in new[]{("finishingExposureEv",.2),("finishingVibrance",1.2),("finishingCurveMidtones",.57)}) values[key]!.GetType().GetProperty("Value",All)!.SetValue(values[key],value);
}
var before=JsonSerializer.Serialize(Call(view,"CaptureSettings"));
var cube=((Task<string>)Call(view,"ExportColorLutAsync",look,33,CancellationToken.None)!).GetAwaiter().GetResult();
var nodes=cube.Split('\n').Where(x=>x.Length>0 && char.IsAsciiDigit(x[0])).Select(x=>x.Split(' ').Select(v=>double.Parse(v,CultureInfo.InvariantCulture)).ToArray()).ToArray();
var grid=(byte[])Call(T("NativeColorLut"),"CreateGrid",33)!;
var choices=new[]{0,1,16,32,33,544,1088,1089,16335,17968,17969,21000,31000,35000,35936};
var sparse=new byte[choices.Length*16];
for(int j=0;j<choices.Length;j++)Array.Copy(grid,choices[j]*16,sparse,j*16,16);
var options=(string[])Call(view,"RendererOptions",look,"srgb","srgb",true,false)!;
Check(!options.Any(x=>x.Contains("finishing",StringComparison.Ordinal)),"Finishing leaked into worker args");
options=(string[])Call(T("NativeColorLut"),"ColorOnlyOptions",(object)options)!;
using var worker=(IDisposable)Activator.CreateInstance(T("OpticalRenderer"),All,null,new object[]{true,false},null)!;
var rendered=strength>0?((Task<byte[]>)Call(worker,"RenderAsync",choices.Length,1,sparse,options,CancellationToken.None,false)!).GetAwaiter().GetResult():null;
var frame=Activator.CreateInstance(T("HostImageSource+Frame"),All,null,new object[]{choices.Length,1,Array.Empty<byte>(),sparse,32},null)!;
var finishing=Activator.CreateInstance(T("FinishingTransform"),new[]{Call(view,"CaptureFinishingSettings")})!;
var bitmap=(BitmapSource)Call(T("SpatialPreview"),"DecodeExportWithFinishing",frame,rendered,strength,Enum.Parse(T("PreviewInputSpace"),"Srgb"),"native_srgb",PixelFormats.Rgb48,new System.Drawing.Rectangle(0,0,choices.Length,1),CancellationToken.None,finishing)!;
var pixels=new ushort[choices.Length*3];bitmap.CopyPixels(pixels,pixels.Length*2,0);
double worst=0;
for(int i=0;i<choices.Length;i++)for(int c=0;c<3;c++) {
 var delta=Math.Abs(Math.Round(nodes[choices[i]][c]*65535)-pixels[i*3+c]);worst=Math.Max(worst,delta);
 Check(delta<=1,$"Node does not match independently arranged render: {choices[i]} / {c}, delta {delta}");
 if(strength==0)Check(Math.Abs(nodes[choices[i]][c]-MemoryMarshal.Cast<byte,float>(sparse.AsSpan())[i*4+c])<1e-9,"Zero-strength LUT is not identity");
}
Check(JsonSerializer.Serialize(Call(view,"CaptureSettings"))==before,"LUT changed state");
Console.WriteLine($"PASS {mode}, strength={strength}, finishing={finished}: all 45 selected channels agree with direct RGB16 render; max delta {worst}");
}
}
