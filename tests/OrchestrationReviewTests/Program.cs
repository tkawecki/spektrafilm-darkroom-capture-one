using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Program
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static int checks;
    private static void Check(bool pass,string reason){checks++;if(!pass)throw new Exception(reason);}
    private static object? Call(object target,string name,params object?[] args)=>(target is Type t?t:target.GetType()).GetMethod(name,All)!.Invoke(target is Type?null:target,args);
    private static string Json(object? value)=>JsonSerializer.Serialize(value);
    private static IDictionary Table(object settings,string key)=>(IDictionary)settings.GetType().GetProperty(key,All)!.GetValue(settings)!;
    [STAThread] private static int Main()
    {
        const string c1=@"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
        // The staged runtime (stocks.json, renderer/, profiles/): SPEKTRAFILM_TEST_RUNTIME, else a local test copy, else the repository root after Build-Windows.ps1 -StageRuntime.
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT",Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_TEST_RUNTIME") is { Length: > 0 } staged ? staged : Directory.Exists("artifacts/effects-refresh/runtime") ? "artifacts/effects-refresh/runtime" : "."));
        var panel=Assembly.Load("NativePanel");var type=panel.GetType("SpatialPreview",true)!;var lookType=panel.GetType("LookSettings",true)!;
        var look=JsonSerializer.Deserialize("{\"Film\":\"kodak_portra_400\",\"Paper\":\"kodak_portra_endura\",\"Mode\":\"print\",\"SpectralMethod\":\"hanatos2025\",\"Exposure\":0.4,\"PrintExposure\":0,\"Strength\":1,\"Development\":{}}",lookType)!;
        var getter=Expression.Lambda(typeof(Func<>).MakeGenericType(lookType),Expression.Constant(look,lookType)).Compile();
        object Create()=>Call(type,"BuildForPanel",new object(),getter,new Func<Guid,bool>(_=>true))!;
        var a=Create();var b=Create();
        try
        {
            int changed=0;type.GetEvent("SettingsChanged",All)!.GetAddMethod(true)!.Invoke(a,new object[]{new Action(()=>changed++)});
            var settings=Call(a,"CaptureSettings")!;var selection=Table(settings,"Selections");var numbers=Table(settings,"Numbers");
            selection["export-format"]="dng";selection["export-color"]="adobe_rgb";
            selection["finishing-curve-red-points"]="[[0,0],[0.137,0.29],[1,1]]";
            numbers["finishingGamma"]=1.7;numbers["finishingMicroContrast"]=.4;
            var bBefore=Json(Call(b,"CaptureSettings"));Call(a,"LoadSettings",settings);
            var saved=Call(a,"CaptureSettings")!;
            foreach(var key in new[]{"export-format","export-color"})Check(Equals(Table(saved,"Selections")[key],selection[key]),"Saved selection lost: "+key);
            Check(((string)Table(saved,"Selections")["finishing-curve-red-points"]!).Contains("0.137"),"Arbitrary curve X lost");
            Check((double)Table(saved,"Numbers")["finishingGamma"]! ==1.7,"Finishing value changed");
            Check(changed==0,"Loading saved settings raised persistence event");
            Check(Json(Call(b,"CaptureSettings"))==bBefore,"Loading A modified independent context B");
            var stable=Json(saved);Call(a,"LoadSettings",saved);Check(Json(Call(a,"CaptureSettings"))==stable,"Repeated load changes a saved snapshot");
            var invalid=Call(a,"CaptureSettings")!;Table(invalid,"Selections")["finishing-curve-master-points"]="[[0,0],[0.5,99],[1,1]]";
            try{Call(a,"LoadSettings",invalid);throw new Exception("Invalid curve accepted");}catch(TargetInvocationException e)when(e.InnerException is ArgumentException or InvalidDataException or FormatException){checks++;}
            Check(Json(Call(a,"CaptureSettings"))==stable,"Rejected curve partially changed controls");
            foreach(var target in new[]{"-1.5","0","0.25","2"})
            {
                var args=(string[])Call(a,"RendererOptions",look,"prophoto","linear_rec709",true,false)!;
                Call(type,"OverrideOption",args,"--film-exposure",target);
                Check(args[Array.IndexOf(args,"--film-exposure")+1]==target,"Exposure strip override was not applied");
                Check(args[Array.IndexOf(args,"--output-color")+1]=="linear_rec709","Target override changes renderer encoding");
            }
            Check(Json(Call(a,"CaptureSettings"))==stable,"Export argument preparation mutated stored settings");
            var empty=Activator.CreateInstance(panel.GetType("SpatialPreview+Settings",true)!,new Dictionary<string,double>(),new Dictionary<string,string>(),new Dictionary<string,bool>())!;
            Call(a,"LoadSettings",empty);var legacy=Call(a,"CaptureSettings")!;
            Check((string)Table(legacy,"Selections")["input-color"]! =="srgb","Missing historical input changed to new default");
            Check((string)Table(legacy,"Selections")["input-preparation"]! =="capture-one","Missing historical preparation changed");
            Call(a,"LoadSettings",legacy);Check(Json(Call(a,"CaptureSettings"))==Json(legacy),"Historical fallback changed after roundtrip");
            Console.WriteLine($"PASS {checks} production orchestration assertions; no C1/window shown");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e.GetBaseException());return 1;}
        finally{((IDisposable)a).Dispose();((IDisposable)b).Dispose();}
    }
}
