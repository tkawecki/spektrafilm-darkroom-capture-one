using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Collections;
using System.IO;

const BindingFlags all=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
const string c1=@"C:\Program Files\Capture One\Capture One";
AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
var panel=Assembly.Load("NativePanel");
Type T(string s)=>panel.GetType(s,true)!;
object? Call(object o,string m,params object?[] a)=>(o as Type??o.GetType()).GetMethod(m,all)!.Invoke(o is Type?null:o,a);
object? P(object o,string n)=>o.GetType().GetProperty(n,all)!.GetValue(o);
object Settings(double ev)=>JsonSerializer.Deserialize(JsonSerializer.Serialize(new{
    Look=new{Film="kodak_portra_400",Paper="kodak_portra_endura",Exposure=ev,PrintExposure=.2,Strength=.8,Mode="scan",SpectralMethod="hanatos2025",ScanInvert=true,Development=new Dictionary<string,double>{{"film_gamma",1.2}}},
    Optics=new{Numbers=new Dictionary<string,double>{{"finishingExposureEv",ev}},Selections=new Dictionary<string,string>{{"processing-engine","native"},{"finishing-curve-red-points","[[0,0],[0.137,0.29],[1,1]]"},{"legacy-preset-original",Convert.ToBase64String(new byte[100_000])}},Switches=new Dictionary<string,bool>{{"finishing-enabled",true}}},Enabled=true,Version=1,Grain=new{Type=2,Impact=17d,Granularity=35d}
}),T("VariantLook"))!;
var root=Path.GetFullPath(Path.Combine("artifacts","look-versions",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
var storeType=T("VariantLookStore");
var a=Activator.CreateInstance(storeType,all,null,new object[]{root,Path.Combine(root,"A.cosessiondb")},null)!;
var b=Activator.CreateInstance(storeType,all,null,new object[]{root,Path.Combine(root,"B.cosessiondb")},null)!;
var variant=Guid.NewGuid();var folder=(string)Call(a,"VersionsFolder",variant)!;var other=(string)Call(b,"VersionsFolder",variant)!;
int assertions=0;
void Check(bool ok,string why){assertions++;if(!ok)throw new Exception(why);}
Check(folder!=other,"Document versions collide");
var first=Settings(.3);var second=Settings(-.6);
Call(a,"Queue",variant,first);Call(a,"Flush");var original=JsonSerializer.Serialize(Call(a,"Read",variant));
Call(T("LookVersions"),"Save",folder,variant,"First attempt",first);
Call(T("LookVersions"),"Save",folder,variant,"First attempt",second);
Check(Directory.GetFiles(folder).Length==2,"Same-name versions overwritten");
var versions=((IEnumerable)Call(T("LookVersions"),"Read",folder,variant,null)!).Cast<object>().ToArray();
Check(versions.Length==2,"Version enumeration missing");
Check(JsonSerializer.Serialize(P(versions[0],"Settings"))==JsonSerializer.Serialize(second),"Newest full settings not restored");
Check(JsonSerializer.Serialize(P(versions[1],"Settings"))==JsonSerializer.Serialize(first),"Old values or metadata changed");
Check(JsonSerializer.Serialize(Call(a,"Read",variant))==original,"Saving versions rewrote active variant");
((IDictionary)P(P(first,"Optics")!,"Numbers")!)["finishingExposureEv"]=7d;
var again=((IEnumerable)Call(T("LookVersions"),"Read",folder,variant,null)!).Cast<object>().ToArray();
Check((double)((IDictionary)P(P(P(again[1],"Settings")!,"Optics")!,"Numbers")!)["finishingExposureEv"]! == .3,"Version snapshot aliased live controls");
File.WriteAllText(Path.Combine(folder,"broken.json"),"{broken");var warnings=new List<string>();
var valid=((IEnumerable)Call(T("LookVersions"),"Read",folder,variant,new Action<string>(warnings.Add))!).Cast<object>().ToArray();
Check(valid.Length==2&&warnings.Count==1,"Corrupt entry blocked history or disappeared silently");
Check(!((IEnumerable)Call(T("LookVersions"),"Read",folder,Guid.NewGuid(),new Action<string>(warnings.Add))!).Cast<object>().Any(),"Wrong variant history accepted");
Check(!((IEnumerable)Call(T("LookVersions"),"Read",other,variant,null)!).Cast<object>().Any(),"Other document inherited history");
Call(a,"Queue",variant,P(valid[1],"Settings"));Call(a,"Flush");
Check(JsonSerializer.Serialize(Call(a,"Read",variant))==original,"Version restore changed saved numbers/strings/toggles");
Console.WriteLine($"PASS {assertions} history assertions: immutable versions, duplicate labels, full metadata/hidden settings, corruption and document isolation");
