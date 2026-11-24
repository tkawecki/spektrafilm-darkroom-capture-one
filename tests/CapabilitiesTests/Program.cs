using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Expression=System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;

internal static class Program
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool result,string message) { checks++; if(!result)throw new Exception(message); }
    [STAThread] static int Main()
    {
        // The staged runtime (stocks.json, renderer/, profiles/): SPEKTRAFILM_TEST_RUNTIME, else a local test copy, else the repository root after Build-Windows.ps1 -StageRuntime.
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT",Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_TEST_RUNTIME") is { Length: > 0 } staged ? staged : Directory.Exists("artifacts/effects-refresh/runtime") ? "artifacts/effects-refresh/runtime" : "."));
        // Global Viewer preferences live next to panel.log; use an isolated scratch folder.
        var preferencesFolder=Path.GetFullPath(Path.Combine("artifacts","capabilities-tests",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(preferencesFolder);
        Environment.SetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG",Path.Combine(preferencesFolder,"panel.log"));
        const string c1=@"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
        var assembly=Assembly.Load("NativePanel");var type=assembly.GetType("SpatialPreview",true)!;var lookType=assembly.GetType("LookSettings",true)!;
        object Look(string mode)=>JsonSerializer.Deserialize("{\"Film\":\"kodak_portra_400\",\"Paper\":\"kodak_portra_endura\",\"Mode\":\""+mode+"\",\"SpectralMethod\":\"hanatos2025\",\"Exposure\":.4,\"PrintExposure\":0,\"Strength\":1,\"Development\":{}}".Replace(":.4",":0.4"),lookType)!;
        var holder=new StrongBox<object>(Look("print"));
        var callback=Expression.Lambda(typeof(Func<>).MakeGenericType(lookType),Expression.Convert(Expression.Field(Expression.Constant(holder),"Value"),lookType)).Compile();
        var view=type.GetMethod("BuildForPanel",Flags)!.Invoke(null,new object[]{new object(),callback,new Func<Guid,bool>(_=>true)})!;
        object? Call(string method,params object?[] args)=>type.GetMethod(method,Flags)!.Invoke(view,args);
        bool Enabled(string table,string key)=>((FrameworkElement)((IDictionary)type.GetField(table,Flags)!.GetValue(view)!)[key]!).IsEnabled;
        object Control(string table,string key)=>((IDictionary)type.GetField(table,Flags)!.GetValue(view)!)[key]!;
        void SetValue(string key,double value){var slider=Control("values",key);slider.GetType().GetProperty("Value",Flags)!.SetValue(slider,value);}
        void SetChoice(string key,string id){var box=(System.Windows.Controls.ComboBox)Control("choices",key);foreach(var item in box.ItemsSource){if((string)item.GetType().GetProperty("Id")!.GetValue(item)==id){box.SelectedItem=item;return;}}throw new Exception("Missing choice "+id);}
        void SetToggle(string key,bool value)=>((System.Windows.Controls.CheckBox)Control("toggles",key)).IsChecked=value;
        bool PanelEnabled(string field)=>((FrameworkElement)type.GetField(field,Flags)!.GetValue(view)!).IsEnabled;
        try {
            ViewerPreferences();
            HonestGating();
            // Mode checks below need an active halation and grain configuration.
            SetValue("halation-amount",1);SetChoice("grain-model","preview");
            Call("UpdateCapabilities");
            var saved=JsonSerializer.Serialize(Call("CaptureSettings"));
            foreach(var mode in new[]{"print","scan","print"}) {
                holder.Value=Look(mode);Call("UpdateCapabilities");
                Check(Enabled("toggles","custom-halation-rgb"),mode+" custom-halation-rgb");
                foreach(var key in new[]{"auto-exposure","color-input-compression"})Check(Enabled("toggles",key),mode+" "+key);
                foreach(var key in new[]{"halation-amount","scatter-amount","grain-amount","dir-diffusion"})Check(Enabled("values",key),mode+" "+key);
                foreach(var key in new[]{"camera-diffusion-family","grain-model"})Check(Enabled("choices",key),mode+" "+key);
                Check(Enabled("choices","print-diffusion-family")==(mode=="print"),mode+" print diffusion needs a printing stage");
                Check(Enabled("values","film-base-scale"),"Film base became disabled");
                Check(Enabled("values","finishingExposureEv"),"Finishing disabled");
                Check(JsonSerializer.Serialize(Call("CaptureSettings"))==saved,"Mode capability changes mutated saved settings");
            }
            MeteringRow();
            // A look saved with the removed second engine is refused, not reinterpreted as Native.
            var removed=JsonSerializer.Deserialize("{\"Numbers\":{},\"Selections\":{\"processing-engine\":\"dyeworks-2.2.3\"},\"Switches\":{}}",type.GetNestedType("Settings",Flags)!)!;
            bool refused=false;
            try{type.GetMethod("RejectRemovedEngine",Flags)!.Invoke(null,new[]{removed});}
            catch(System.Reflection.TargetInvocationException e)when(e.InnerException is InvalidDataException){refused=true;}
            Check(refused,"a look of the removed engine must be refused");
            type.GetMethod("RejectRemovedEngine",Flags)!.Invoke(null,new object?[]{Call("CaptureSettings")});
            // DIR tone warning: silent up to 1.5 effective strength (all measured settings monotonic there),
            // shown above it; at default inhibitions the effective strength is the amount itself.
            var guard=assembly.GetType("DirToneGuard",true)!;
            string? DirWarning(double a,double s,double i,bool guarded=false)=>(string?)guard.GetMethod("Warning",Flags)!.Invoke(null,new object[]{a,s,i,guarded});
            Check((double)guard.GetMethod("EffectiveStrength",Flags)!.Invoke(null,new object[]{1.3,1.0,1.0})! == 1.3,"effective DIR strength must equal the amount at default inhibitions");
            Check(DirWarning(1,1,1) is null&&DirWarning(1.5,1,1) is null&&DirWarning(1,1.5,1.2) is null,"DIR warning shown for settings measured monotonic");
            foreach(var (a,s,i) in new[]{(1.6,1.0,1.0),(1.0,1.0,2.0),(1.0,2.0,1.4),(1.2,2.0,1.0),(1.4,1.5,1.0),(1.5,0.5,1.4)})
                Check(DirWarning(a,s,i) is string text&&text.Contains("reverse tones"),$"DIR warning missing for reversing setting {a}/{s}/{i}");
            // Film model 4 limits the strength in the renderer: the warning says so instead of predicting reversed tones.
            Check(DirWarning(1.6,1,1,true) is string limited&&limited.Contains("Film model 4")&&!limited.Contains("can reverse tones")&&DirWarning(1.5,1,1,true) is null,"film model 4 DIR warning must describe the limit");
            Console.WriteLine($"PASS {checks} Print/Scan capability, honest gating and preservation assertions.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error.GetBaseException());return 1;}
        finally{((IDisposable)view).Dispose();}

        // Viewer resolution, drag size, draft synthesis and Show source are global, not per look.
        void ViewerPreferences()
        {
            var settingsType=type.GetNestedType("Settings",Flags)!;
            object Saved(string viewer,int drag,bool draft,bool show)=>JsonSerializer.Deserialize("{\"Numbers\":{},\"Selections\":{\"preview-size\":\""+viewer+"\",\"drag-preview-size\":\""+drag+"\"},\"Switches\":{\"draft-synthesis\":"+(draft?"true":"false")+",\"show-source\":"+(show?"true":"false")+"}}",settingsType)!;
            string Viewer(){var item=((System.Windows.Controls.ComboBox)type.GetField("previewSize",Flags)!.GetValue(view)!).SelectedItem;return (string)item.GetType().GetProperty("Id")!.GetValue(item)!;}
            int Drag()=>(int)type.GetProperty("DragLongEdge",Flags)!.GetValue(view)!;
            bool Draft()=>((System.Windows.Controls.CheckBox)type.GetField("fastSynthesis",Flags)!.GetValue(view)!).IsChecked==true;
            var file=Path.Combine(preferencesFolder,"Panel preferences.json");
            Check(!File.Exists(file),"preferences existed before migration");
            Call("LoadSettings",Saved("1600",2560,true,false));
            Check(File.Exists(file)&&Viewer()=="1600"&&Drag()==2560&&Draft(),"first saved look did not seed Viewer preferences");
            Call("LoadSettings",Saved("720",720,false,false));
            Check(Viewer()=="1600"&&Drag()==2560&&Draft(),"selecting another look changed global Viewer settings");
            var box=(System.Windows.Controls.ComboBox)type.GetField("previewSize",Flags)!.GetValue(view)!;
            foreach(var item in box.ItemsSource)if((string)item.GetType().GetProperty("Id")!.GetValue(item)=="3072")box.SelectedItem=item;
            Check(File.ReadAllText(file).Contains("\"3072\""),"changing the Viewer size was not saved globally");
            Call("set_CompareSource",true);Call("LoadSettings",Saved("1200",1200,false,false));
            Check((bool)Call("get_CompareSource")!&&Viewer()=="3072","loading a look reset Show source or Viewer size");
            Call("set_CompareSource",false);
            ((System.Windows.Controls.CheckBox)type.GetField("fastSynthesis",Flags)!.GetValue(view)!).IsChecked=false;
            Check(!File.ReadAllText(file).Contains("\"DraftSynthesis\": true"),"draft synthesis preference not saved globally");
            Directory.Delete(preferencesFolder,true);
        }

        // Automatic film exposure and its metering method form one row under Defaults (2026-11-21), outside the
        // collapsible optics sections; the method is inactive while the exposure is not metered.
        void MeteringRow()
        {
            var row=(FrameworkElement)type.GetProperty("MeteringControls",Flags)!.GetValue(view)!;
            var optics=(FrameworkElement)type.GetProperty("Element",Flags)!.GetValue(view)!;
            bool Inside(object control,FrameworkElement root){for(var item=control as FrameworkElement;item is not null;item=item.Parent as FrameworkElement)if(ReferenceEquals(item,root))return true;return false;}
            foreach(var (table,key) in new[]{("toggles","auto-exposure"),("choices","auto-exposure-meter")}) {
                Check(Inside(Control(table,key),row),key+" is in the metering row under Defaults");
                Check(!Inside(Control(table,key),optics),key+" is no longer in a Spektrafilm optics section");
            }
            var saved=JsonSerializer.Serialize(Call("CaptureSettings"));
            SetToggle("auto-exposure",false);
            Check(!Enabled("choices","auto-exposure-meter"),"Metering is inactive without automatic film exposure");
            SetToggle("auto-exposure",true);
            Check(Enabled("choices","auto-exposure-meter"),"Metering is active with automatic film exposure");
            Check(JsonSerializer.Serialize(Call("CaptureSettings"))==saved,"Metering row changed the saved settings");
        }
        // Controls the Native renderer ignores in the current configuration must be disabled,
        // without changing any saved value.
        void HonestGating()
        {
            holder.Value=Look("print");
            SetValue("halation-amount",0);SetValue("scatter-amount",0);SetValue("halation-boost",0);SetChoice("grain-model","off");SetToggle("grain-sublayers",true);
            Call("UpdateCapabilities");
            var saved=JsonSerializer.Serialize(Call("CaptureSettings"));
            foreach(var key in new[]{"halation-scale","scatter-scale","halation-boost","halation-boost-range","halation-protect"})Check(!Enabled("values",key),"amount 0 leaves "+key+" active");
            Check(!Enabled("toggles","custom-halation-rgb"),"amount 0 leaves halation override active");
            foreach(var key in new[]{"grain-amount","grain-saturation","grain-final-blur","grain-seed"})Check(!Enabled("values",key),"grain Off leaves "+key+" active");
            Check(!PanelEnabled("particleGrain")&&!PanelEnabled("synthesisGrain"),"grain Off leaves model sections active");
            Check(Enabled("choices","film-format"),"film format must stay active: it scales optics");
            Check(JsonSerializer.Serialize(Call("CaptureSettings"))==saved,"gating mutated saved settings");
            SetValue("halation-amount",1);Call("UpdateCapabilities");
            Check(Enabled("values","halation-scale")&&!Enabled("values","scatter-scale")&&Enabled("values","halation-boost"),"halation enables its own controls only");
            Check(!Enabled("values","halation-protect"),"protection needs boost > 0");
            SetValue("halation-boost",2);Call("UpdateCapabilities");
            Check(Enabled("values","halation-protect")&&Enabled("values","halation-boost-range"),"boost enables range/protection");
            SetChoice("grain-model","production");Call("UpdateCapabilities");
            Check(PanelEnabled("particleGrain")&&!PanelEnabled("synthesisGrain")&&Enabled("values","grain-amount"),"production enables particle grain only");
            Check(!Enabled("values","grain-sublayer-count"),"sublayer count inactive while fixed sublayers are on");
            SetToggle("grain-sublayers",false);Call("UpdateCapabilities");
            Check(Enabled("values","grain-sublayer-count"),"sublayer count active when fixed sublayers are off");
            SetChoice("grain-model","synthesis");Call("UpdateCapabilities");
            Check(!PanelEnabled("particleGrain")&&PanelEnabled("synthesisGrain"),"synthesis enables its own section only");
            Check(!Enabled("values","grain-amount")&&!Enabled("values","grain-saturation"),"synthesis does not read grain amount or saturation");
            SetChoice("grain-model","rms");Call("UpdateCapabilities");
            Check(!Enabled("values","grain-particle-area")&&!Enabled("values","grain-particle-scale-r")&&Enabled("values","grain-amount"),"RMS replaces particle area and scales");
            SetChoice("grain-model","production");SetToggle("grain-sublayers",false);Call("UpdateCapabilities");
            Check(!Enabled("values","grain-dye-cloud-blur")&&!Enabled("values","grain-microstructure")&&Enabled("values","grain-particle-area"),"dye clouds and microstructure need sublayers");
            SetToggle("grain-sublayers",true);Call("UpdateCapabilities");
            Check(Enabled("values","grain-dye-cloud-blur"),"sublayers enable dye clouds");
            SetValue("dir-diffusion",0);Call("UpdateCapabilities");
            Check(Enabled("values","dir-diffusion")&&!Enabled("values","dir-tail-size")&&!Enabled("values","dir-tail-weight"),"DIR tail needs diffusion width");
            SetValue("dir-diffusion",20);Call("UpdateCapabilities");
            Check(Enabled("values","dir-tail-size")&&Enabled("values","dir-tail-weight"),"DIR diffusion enables the tail");
            SetValue("dir-diffusion",0);SetChoice("grain-model","synthesis");Call("UpdateCapabilities");
            // Desqueeze is limited to |v| >= 1; the slider skips the degenerate range.
            double Desqueeze(double v){SetValue("finishingDesqueeze",v);var c=Control("values","finishingDesqueeze");return (double)c.GetType().GetProperty("Value",Flags)!.GetValue(c)!;}
            Check(Desqueeze(0.5)==1&&Desqueeze(0.3)==0&&Desqueeze(-0.7)==-1&&Desqueeze(-0.2)==0&&Desqueeze(1.33)==1.33&&Desqueeze(-1.5)==-1.5,"desqueeze slider must skip values between -1 and 1");
            Check((double)type.GetMethod("EffectiveDesqueeze",Flags)!.Invoke(null,new object[]{0.01})! == 1,"older looks with |v| < 1 must not squeeze the image");
            SetValue("finishingDesqueeze",1);
            // Integer renderer options are rounded even when a dragged slider reports a fraction.
            SetValue("grain-seed",20881.931373);SetValue("grain-synthesis-samples",127.6);
            var args=(string[])type.GetMethod("RendererOptions",Flags)!.Invoke(view,new object?[]{holder.Value,"prophoto","srgb",false,false})!;
            string Arg(string name){var i=Array.IndexOf(args,"--"+name);return i<0?"":args[i+1];}
            Check(Arg("grain-seed")=="20882"&&Arg("grain-synthesis-samples")=="128","integer options not rounded: "+Arg("grain-seed")+" "+Arg("grain-synthesis-samples"));
            Check(Arg("halation-amount")=="1","fractional options must stay unrounded");
            // Grain mean restore: on for new looks, sent to Native only, enabled only with grain on.
            // The view currently holds a look loaded without the key (ViewerPreferences): it keeps the old behaviour.
            Check(Arg("grain-mean-restore")=="false","a look without the key must keep the original grain behaviour");
            Check(Arg("grain-synthesis-aperture")=="false","a look without the key must keep point-sampled Grain synthesis");
            Check(Arg("grain-synthesis-noise-match")=="false","a look without the key must keep Grain synthesis sampling noise");
            Call("UpdateCapabilities");Check(!Enabled("toggles","grain-synthesis-noise-match"),"noise matching active without aperture integration");
            Check(Arg("grain-synthesis-transmission-blur")=="false","a look without the key must keep the density-domain final blur");
            SetChoice("grain-model","off");Call("UpdateCapabilities");
            Check(!Enabled("toggles","grain-mean-restore"),"grain mean restore active with grain off");
            var oldLook=JsonSerializer.Deserialize("{\"Numbers\":{},\"Selections\":{\"quality-defaults\":\"2\"},\"Switches\":{}}",type.GetNestedType("Settings",Flags)!)!;
            Call("LoadSettings",oldLook);
            Check(((System.Windows.Controls.CheckBox)Control("toggles","grain-mean-restore")).IsChecked==false,"a look saved before the restore existed changed its grain behaviour");
            Call("LoadSettings",new object?[]{null});
            Check(((System.Windows.Controls.CheckBox)Control("toggles","grain-mean-restore")).IsChecked==true,"Defaults must enable the grain mean restore");
            SetChoice("grain-model","production");
            var defaultArgs=(string[])type.GetMethod("RendererOptions",Flags)!.Invoke(view,new object?[]{holder.Value,"prophoto","srgb",false,false})!;
            Check(defaultArgs[Array.IndexOf(defaultArgs,"--grain-mean-restore")+1]=="true","new looks must send the grain mean restore to Native");
            // Grain synthesis aperture integration (2026-11-18): on for new looks, off for looks saved without it.
            Check(defaultArgs[Array.IndexOf(defaultArgs,"--grain-synthesis-aperture")+1]=="true","new looks must send Grain synthesis aperture integration");
            Check(((System.Windows.Controls.CheckBox)Control("toggles","grain-synthesis-aperture")).IsChecked==true,"Defaults must enable Grain synthesis aperture integration");
            // Grain synthesis noise matching (2026-11-19): on for new looks, off for looks saved without it.
            Check(defaultArgs[Array.IndexOf(defaultArgs,"--grain-synthesis-noise-match")+1]=="true","new looks must send Grain synthesis noise matching");
            // Grain synthesis transmission blur (2026-11-19): on for new looks, off for looks saved without it.
            Check(defaultArgs[Array.IndexOf(defaultArgs,"--grain-synthesis-transmission-blur")+1]=="true","new looks must send the Grain synthesis transmission blur");
            SetChoice("grain-model","synthesis");Call("UpdateCapabilities");
            Check(Enabled("toggles","grain-synthesis-noise-match"),"noise matching inactive with aperture integration on");
            ((System.Windows.Controls.CheckBox)Control("toggles","grain-synthesis-aperture")).IsChecked=false;Call("UpdateCapabilities");
            Check(!Enabled("toggles","grain-synthesis-noise-match"),"noise matching active after aperture integration was turned off");
            ((System.Windows.Controls.CheckBox)Control("toggles","grain-synthesis-aperture")).IsChecked=true;SetChoice("grain-model","production");Call("UpdateCapabilities");
            // Highlight boost protection: relative to the brightest highlight and 2 EV for new looks.
            Check(defaultArgs[Array.IndexOf(defaultArgs,"--halation-protect-relative")+1]=="true"&&defaultArgs[Array.IndexOf(defaultArgs,"--halation-protect")+1]=="2","new looks must use relative 2 EV protection");
            Call("LoadSettings",oldLook);
            Check(((System.Windows.Controls.CheckBox)Control("toggles","halation-protect-relative")).IsChecked==false,"saved looks must keep mid-grey protection");
            Check(((System.Windows.Controls.CheckBox)Control("toggles","grain-synthesis-aperture")).IsChecked==false,"saved looks must keep point-sampled Grain synthesis");
            Check(((System.Windows.Controls.CheckBox)Control("toggles","grain-synthesis-noise-match")).IsChecked==false,"saved looks must keep Grain synthesis sampling noise");
            Check(((System.Windows.Controls.CheckBox)Control("toggles","grain-synthesis-transmission-blur")).IsChecked==false,"saved looks must keep the density-domain final blur");
            var oldArgs=(string[])type.GetMethod("RendererOptions",Flags)!.Invoke(view,new object?[]{holder.Value,"prophoto","srgb",false,false})!;
            Check(oldArgs[Array.IndexOf(oldArgs,"--halation-protect")+1]=="4","a look saved without the key must keep 4 EV protection");
            // Film model (2026-11-23): a look saved without the key is model 1 and sends no option, so it renders as before;
            // Defaults use model 2 (CAT16 input adaptation); an unknown model is refused instead of being reinterpreted.
            Check(Array.IndexOf(oldArgs,"--film-model")<0,"a look saved without a film model must render with model 1 (no option)");
            Check(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(Call("CaptureSettings"))).GetProperty("Selections").GetProperty("film-model").GetString()=="1","an old look must be saved back as model 1");
            Check(defaultArgs[Array.IndexOf(defaultArgs,"--film-model")+1]=="4","new looks must use film model 4");
            var futureLook=JsonSerializer.Deserialize("{\"Numbers\":{},\"Selections\":{\"quality-defaults\":\"2\",\"film-model\":\"9\"},\"Switches\":{}}",type.GetNestedType("Settings",Flags)!)!;
            var refused=false;try{Call("LoadSettings",futureLook);}catch(Exception error)when(error.GetBaseException() is InvalidDataException){refused=true;}
            Check(refused,"a look with an unknown film model must be refused, not reinterpreted");
            Call("LoadSettings",new object?[]{null});SetChoice("grain-model","production");
            SetChoice("grain-model","synthesis");
            // Recommended input only changes the two input selections.
            SetToggle("grain-sublayers",true);
            Dictionary<string,string> Selections()=>JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(Call("CaptureSettings"))).GetProperty("Selections").Deserialize<Dictionary<string,string>>()!;
            var before=Selections();
            Call("ApplyRecommendedInput");
            var after=Selections();
            Check(after["input-preparation"]=="neutral-film"&&after["input-color"]=="prophoto","recommended input not applied");
            foreach(var (key,value) in before)if(key is not ("input-preparation" or "input-color"))Check(after[key]==value,"recommended input changed "+key);
            // Restore the state used by the original mode checks.
            SetValue("halation-boost",0);SetValue("grain-seed",1);SetValue("grain-synthesis-samples",128);
        }
    }
}
