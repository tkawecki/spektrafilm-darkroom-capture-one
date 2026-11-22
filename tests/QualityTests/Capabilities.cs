using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

partial class Program
{
    static object Look(string mode="print", bool invert=false, double exhaustion=0) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(new {
            Film="kodak_portra_400",Paper="kodak_portra_endura",Exposure=0d,PrintExposure=0d,Strength=1d,
            Mode=mode,SpectralMethod="hanatos2025",ScanInvert=invert,
            Development=new Dictionary<string,double>{{"print_exhaustion",exhaustion}},FilmPushPullMode="standard"
        }),panel.GetType("LookSettings",true)!)!;

    static object View(Func<object> current)
    {
        var lookType=panel.GetType("LookSettings",true)!;
        var callback=Expression.Lambda(typeof(Func<>).MakeGenericType(lookType),
            Expression.Convert(Expression.Invoke(Expression.Constant(current)),lookType)).Compile();
        return Invoke("SpatialPreview","BuildForPanel",new object(),callback,new Func<Guid,bool>(_=>true));
    }
    static object? Member(object value,string name) => value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value);
    static object? CallView(object view,string name,params object?[] args) => view.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(view,args);

    static void Exhaustion()
    {
        var current=Look();var view=View(()=>current);
        try
        {
            foreach(var mode in new[]{"print","scan"}) foreach(var export in new[]{false,true})
            {
                current=Look(mode,exhaustion:0);
                var expected=(string[])CallView(view,"RendererOptions",current,"prophoto","srgb",export,false)!;
                current=Look(mode,exhaustion:.7);
                var saved=JsonSerializer.Serialize(current);
                var actual=(string[])CallView(view,"RendererOptions",current,"prophoto","srgb",export,false)!;
                if (mode=="scan") Check(actual.SequenceEqual(expected),"Print exhaustion changed Scan options");
                else {
                    var key=Array.IndexOf(actual,"--print-exhaustion");
                    Check(key>=0 && actual[key+1]=="0.7","Exhaustion was not mapped to renderer");
                    Check(!actual.SequenceEqual(expected),"Print exhaustion ignored");
                }
                Check(JsonSerializer.Serialize(current)==saved,"Saved exhaustion was erased");
            }
        }
        finally {((IDisposable)view).Dispose();}
    }

    static void Capabilities()
    {
        var current=Look();var view=View(()=>current);
        try
        {
            var numbers=(IDictionary)Member(view,"values")!;
            var choices=(IDictionary)Member(view,"choices")!;
            var toggles=(IDictionary)Member(view,"toggles")!;
            FrameworkElement N(string key)=>(FrameworkElement)numbers[key]!;
            CheckBox T(string key)=>(CheckBox)toggles[key]!;
            ComboBox C(string key)=>(ComboBox)choices[key]!;
            bool Enabled(string key)=>N(key).IsEnabled;
            void Refresh()=>CallView(view,"RequestRefresh");
            void Value(string key,double value)=>N(key).GetType().GetProperty("Value",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.SetValue(N(key),value);
            string State()=>JsonSerializer.Serialize(CallView(view,"CaptureSettings"));
            Check(!Enabled("scanner-mtf")&&!T("scanner-white").IsEnabled,"Scanner Off leaves its controls active");
            T("scanner-enabled").IsChecked=true;
            Check(Enabled("scanner-mtf")&&T("scanner-white").IsEnabled&&Enabled("scanner-glare"),"Print scanner failed to enable");
            Check(!Enabled("scanner-white-level")&&!Enabled("scanner-black-level"),"Unchecked correction remains active");
            T("scanner-white").IsChecked=true;T("scanner-black").IsChecked=true;
            Value("scanner-white-level",.73);Value("scanner-black-level",.12);
            C("camera-diffusion-family").SelectedIndex=1;C("print-diffusion-family").SelectedIndex=1;
            Value("print-diffusion-strength",1.375);
            var saved=State();
            current=Look("scan");Refresh();
            Check(!C("print-diffusion-family").IsEnabled&&!Enabled("print-diffusion-strength"),"Print diffusion active in Scan");
            Check(Enabled("camera-diffusion-strength"),"Camera diffusion incorrectly disabled in Scan");
            Check(!T("scanner-white").IsEnabled&&!Enabled("scanner-white-level")&&!Enabled("scanner-black-level"),"Uninverted Scan exposes inactive levels");
            Check(!Enabled("scanner-glare")&&!Enabled("scanner-glare-blur")&&Enabled("scanner-mtf"),"Scan scanner capabilities wrong");
            Check(State()==saved,"Switching modes changed saved optical values");
            current=Look("scan",true);Refresh();
            Check(T("scanner-white").IsEnabled&&Enabled("scanner-white-level")&&Enabled("scanner-black-level"),"Inverted Scan cannot adjust levels");
            Check(!Enabled("scanner-glare"),"Inverted Scan exposes ignored glare");
            current=Look();Refresh();
            Check(C("print-diffusion-family").IsEnabled&&Enabled("print-diffusion-strength")&&Enabled("scanner-glare"),"Print settings not re-enabled");
            Check(State()==saved,"Inactive values not retained");
            T("scanner-enabled").IsChecked=false;
            Check(!Enabled("scanner-mtf")&&!Enabled("scanner-white-level"),"Scanner Off did not disable controls");
            var retained=CallView(view,"CaptureSettings");CallView(view,"LoadSettings",(object?)null);
            CallView(view,"LoadSettings",retained);T("scanner-enabled").IsChecked=true;
            Check(State()==saved,"Stored inactive values failed roundtrip");
        }
        finally {((IDisposable)view).Dispose();}
    }
}
