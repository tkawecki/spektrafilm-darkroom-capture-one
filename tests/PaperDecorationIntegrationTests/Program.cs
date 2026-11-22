using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Assembly panel=null!;
    private static Type preview=null!,frameType=null!,inputType=null!,outputType=null!;
    private static readonly string folder=Path.GetFullPath(Path.Combine("artifacts/full-function-20261116/paper-integration",Guid.NewGuid().ToString("N")));
    private static readonly List<string> ids=new();
    private static object[] papers=null!,geometries=null!;
    private static JsonDocument data=null!;
    private static object frame=null!;
    private static byte[] source=null!,render=null!;
    private static Rectangle crop;
    private static int checks,failures,files,oracleSamples;
    private static double maxError;
    [STAThread] private static int Main(string[] args)
    {
        const string c1=@"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
        string assemblyPath=Path.GetFullPath(args.Length>0?args[0]:"build/full-function/panel/NativePanel.dll");
        panel=AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        preview=T("SpatialPreview");frameType=T("HostImageSource+Frame");inputType=T("PreviewInputSpace");outputType=T("ExportRgbSpace");
        var previous=Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");var previousLog=Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG");
        Directory.CreateDirectory(folder);Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT",Path.Combine(folder,"runtime"));
        // Paper textures live in user data next to panel.log; keep them inside this test folder.
        Environment.SetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG",Path.Combine(folder,"user-data","panel.log"));
        try
        {
            Setup();
            Test("Production finishing, micro, C1 crop, geometry and paper match independent reference oracle in all 17 targets",Oracle);
            Test("Preview and RGB16 wrappers preserve exact helper pixels, dimensions, alpha and profiles",Wrappers);
            Test("Neutral paper delegates old preview and all four writers byte-exactly",Neutral);
            Test("JPEG/DNG routing, paper range clamp and RGB16 DNG payload",OtherFormats);
            Test("Missing active assets and cancellation fail before destination mutation",Errors);
            File.WriteAllText(Path.Combine(folder,"verification.json"),JsonSerializer.Serialize(new{checks,failures,files,oracleSamples,maxError,
                panelSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),fixtureSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures.json"))))},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"{checks} assertions; {failures} failing groups; {oracleSamples} oracle samples; max error {maxError:E6}; {files} files; {folder}");
        }
        finally { Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT",previous);Environment.SetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG",previousLog);data?.Dispose(); }
        return failures==0?0:1;
    }
    private static Type T(string name)=>panel.GetType(name,true)!;
    // Exact parameter count first; otherwise the overload whose remaining parameters are optional (their defaults are passed).
    private static object? Call(object target,string name,params object?[] args)
    {
        var methods=(target as Type??target.GetType()).GetMethods(All).Where(m=>m.Name==name).ToArray();
        var method=methods.SingleOrDefault(m=>m.GetParameters().Length==args.Length)
            ??methods.Single(m=>m.GetParameters().Length>args.Length&&m.GetParameters().Skip(args.Length).All(p=>p.IsOptional));
        var full=args.Concat(method.GetParameters().Skip(args.Length).Select(p=>p.DefaultValue)).ToArray();
        return method.Invoke(target is Type?null:target,full);
    }
    private static object Prop(object value,string name)=>value.GetType().GetProperty(name,All)!.GetValue(value)!;
    private static object New(Type type,params object?[] args)=>Activator.CreateInstance(type,All,null,args,null)!;
    private static object EnumValue(Type type,string value)=>Enum.Parse(type,value);
    private static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    private static void Test(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException());}}
    private static void Reject<TError>(Action action) where TError:Exception
    {try{action();}catch(Exception error) when(error.GetBaseException() is TError){checks++;return;}throw new Exception("Expected "+typeof(TError).Name);}
    private static byte[] Bytes(float[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static float[] Floats(JsonElement values)=>values.EnumerateArray().Select(x=>x.GetSingle()).ToArray();
    private static object Finish(bool active,double micro)
    {
        var values=new Dictionary<string,double>{{"finishingMicroContrast",micro}};
        if(active){values["finishingGamma"]=1.7;values["finishingContrast"]=.2;values["finishingSaturation"]=.65;}
        return New(T("FinishingTransform"),Call(T("FinishingSettings"),"FromDictionary",values));
    }
    private static object Finish(JsonElement test)=>Finish(test.GetProperty("finishing").GetBoolean(),test.GetProperty("micro").GetDouble());
    private static object ParsePaper(string json)=>Call(T("PaperDecorationStorage"),"Parse",json)!;
    private static object Paper(JsonElement config)=>ParsePaper(JsonSerializer.Serialize(new{version=1,active=true,paddingBurn=config.GetProperty("burn").GetBoolean(),paddingFraction=config.GetProperty("fraction").GetDouble(),
        layers=config.GetProperty("layers").EnumerateArray().Select(x=>new{asset=ids[x[0].GetInt32()],name="Synthetic audit texture",mode=x[1].GetString(),opacity=x[2].GetDouble()})}));
    private static object Geometry(JsonElement p)=>New(T("ScanGeometrySettings"),p.GetProperty("active").GetBoolean(),p.GetProperty("crop").GetBoolean(),
        p.GetProperty("crop_center")[0].GetDouble(),p.GetProperty("crop_center")[1].GetDouble(),p.GetProperty("crop_size")[0].GetDouble(),p.GetProperty("crop_size")[1].GetDouble(),p.GetProperty("skew_deg").GetDouble(),p.GetProperty("desqueeze").GetDouble());
    private static string? Profile(string name)=>(string?)Call(T("ExportColorSpace"),"ResolveProfileOrNull",EnumValue(outputType,name),Path.GetFullPath("."),null);
    private static void Setup()
    {
        data=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));var root=data.RootElement;
        int w=root.GetProperty("width").GetInt32(),h=root.GetProperty("height").GetInt32();var c=root.GetProperty("crop");crop=new(c[0].GetInt32(),c[1].GetInt32(),c[2].GetInt32(),c[3].GetInt32());
        source=Bytes(Floats(root.GetProperty("source")));render=Bytes(Floats(root.GetProperty("linear")));frame=New(frameType,w,h,Array.Empty<byte>(),source,16);
        frameType.GetProperty("PreviewCrop",All)!.SetValue(frame,crop);
        var store=New(T("PaperTextureStore"),Path.Combine(folder,"user-data","Paper textures"));
        foreach(var item in root.GetProperty("textures").EnumerateArray())
        {
            int tw=item.GetProperty("width").GetInt32(),th=item.GetProperty("height").GetInt32();var rgb=item.GetProperty("rgb").EnumerateArray().Select(x=>x.GetByte()).ToArray();
            string path=Path.Combine(folder,$"texture-{ids.Count}.png");var bitmap=BitmapSource.Create(tw,th,96,96,PixelFormats.Rgb24,null,rgb,tw*3);
            using(var output=File.Create(path)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(output);}
            var asset=Call(store,"Import",path,CancellationToken.None)!;ids.Add((string)Prop(asset,"AssetId"));
            if(ids.Count==2)File.Copy(path,Path.GetFullPath("artifacts/full-function-20261116/paper-texture-fixture.png"),true);
        }
        papers=root.GetProperty("paperConfigs").EnumerateArray().Select(Paper).ToArray();geometries=root.GetProperty("parameters").EnumerateArray().Select(Geometry).ToArray();
    }
    private static (ReadOnlyMemory<float> Pixels,int Width,int Height) Pipeline(JsonElement test,int geometry,int paper)
    {
        var input=EnumValue(inputType,test.GetProperty("input").GetString()!);var output=EnumValue(outputType,test.GetProperty("output").GetString()!);double strength=test.GetProperty("strength").GetDouble();
        var pixels=(float[])Call(preview,"DecodeFinishedFloat",frame,strength==0?null:render,strength,input,"linear_rec709",crop,CancellationToken.None,Finish(test),output)!;
        var transformed=Call(T("ScanGeometry"),"ApplyRgba",new ReadOnlyMemory<float>(pixels),crop.Width,crop.Height,geometries[geometry],CancellationToken.None)!;
        var result=Call(preview,"DecoratePaper",Prop(transformed,"Pixels"),Prop(transformed,"Width"),Prop(transformed,"Height"),papers[paper],CancellationToken.None)!;
        return((ReadOnlyMemory<float>)Prop(result,"Pixels"),(int)Prop(result,"Width"),(int)Prop(result,"Height"));
    }
    private static void Oracle()
    {
        var a=(byte[])source.Clone();var b=(byte[])render.Clone();
        foreach(var test in data.RootElement.GetProperty("cases").EnumerateArray())for(int g=0;g<geometries.Length;g++)for(int p=0;p<papers.Length;p++)
        {
            var result=Pipeline(test,g,p);var expected=test.GetProperty("paper")[g*papers.Length+p];
            Check(result.Width==expected.GetProperty("width").GetInt32()&&result.Height==expected.GetProperty("height").GetInt32(),"reference final canvas dimensions");
            var pixels=expected.GetProperty("rgba");Check(result.Pixels.Length==pixels.GetArrayLength(),"reference RGBA count");
            for(int i=0;i<result.Pixels.Length;i++)
            {
                oracleSamples++;maxError=Math.Max(maxError,Math.Abs(result.Pixels.Span[i]-pixels[i].GetDouble()));
                Check(result.Pixels.Span[i]>=0&&result.Pixels.Span[i]<=1,"Visible paper layers bound final RGB; alpha remains coverage");
            }
        }
        Check(maxError<3e-6,"Final pipeline differs from independent oracle: "+maxError);
        Check(source.SequenceEqual(a)&&render.SequenceEqual(b),"Decoration modified source/render buffers");
    }
    private static JsonElement Case(string output,double strength=.27)=>data.RootElement.GetProperty("cases").EnumerateArray().First(t=>t.GetProperty("input").GetString()=="Srgb"&&t.GetProperty("output").GetString()==output&&t.GetProperty("strength").GetDouble()==strength&&t.GetProperty("finishing").GetBoolean()&&t.GetProperty("micro").GetDouble()==.4);
    private static (int Width,int Height,string Detail) Write(string path,JsonElement test,object geometry,object? paper,CancellationToken token=default)
    {
        double strength=test.GetProperty("strength").GetDouble();string output=test.GetProperty("output").GetString()!;
        object?[] args=[path,frame,strength==0?null:render,strength,EnumValue(inputType,test.GetProperty("input").GetString()!),"linear_rec709",crop,token,Finish(test),EnumValue(outputType,output),Profile(output),92,geometry];
        if(paper is not null)args=args.Append(paper).ToArray();
        var result=((int,int,string))Call(preview,paper is null?"WriteGeometryExportImage":"WritePaperExportImage",args)!;files++;return result;
    }
    private static BitmapSource Preview(JsonElement test,object geometry,object? paper)
    {
        double strength=test.GetProperty("strength").GetDouble();object?[] args=[frame,strength==0?null:render,strength,EnumValue(inputType,test.GetProperty("input").GetString()!),"linear_rec709",CancellationToken.None,Finish(test),geometry];
        if(paper is not null)args=args.Append(paper).ToArray();
        return(BitmapSource)Call(preview,paper is null?"DecodeGeometryPreview":"DecodeDecoratedPreview",args)!;
    }
    private static byte[] Pixels(BitmapSource image){int stride=(image.PixelWidth*image.Format.BitsPerPixel+7)/8;var bytes=new byte[stride*image.PixelHeight];image.CopyPixels(bytes,stride,0);return bytes;}
    private static void EqualBitmap(BitmapSource actual,BitmapSource expected,string what)
    {Check(actual.PixelWidth==expected.PixelWidth&&actual.PixelHeight==expected.PixelHeight&&actual.Format==expected.Format,what+" geometry/precision");Check(Pixels(actual).SequenceEqual(Pixels(expected)),what+" exact pixel bytes");}
    private static void Wrappers()
    {
        foreach(string output in Enum.GetNames(outputType))foreach(double strength in new[]{0d,.27,1d})
        {
            var test=Case(output,strength);var expected=Pipeline(test,0,0);
            foreach(string extension in new[]{"tif","png"})
            {
                string path=Path.Combine(folder,$"active-{output}-{strength}.{extension}");var info=Write(path,test,geometries[0],papers[0]);
                using var stream=File.OpenRead(path);var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                var packed=(BitmapSource)Call(preview,"PackFinishedFloat",expected.Pixels,expected.Width,expected.Height,extension=="png"?PixelFormats.Rgba64:PixelFormats.Rgb48,CancellationToken.None)!;
                EqualBitmap(image,packed,"Target "+output+" "+extension+" "+strength);
                Check(info.Width==image.PixelWidth&&info.Height==image.PixelHeight,"Wrapper reports final paper dimensions");
                string? profile=Profile(output);
                if(profile is null)Check(image.ColorContexts is null||image.ColorContexts.Count==0,"Log output has no false ICC");
                else {using var embedded=image.ColorContexts![0].OpenProfileStream();using var content=new MemoryStream();embedded.CopyTo(content);Check(content.ToArray().SequenceEqual(File.ReadAllBytes(profile)),"Paper changed selected ICC bytes");}
                Check(MemoryMarshal.Cast<byte,ushort>(Pixels(image)).ToArray().Any(x=>x%257!=0),"Photo precision reduced to RGB8");
            }
            if(output=="Srgb")for(int g=0;g<geometries.Length;g++)for(int p=0;p<papers.Length;p++)
            {
                var pixels=Pipeline(test,g,p);var packed=(BitmapSource)Call(preview,"PackFinishedFloat",pixels.Pixels,pixels.Width,pixels.Height,PixelFormats.Bgra32,CancellationToken.None)!;
                EqualBitmap(Preview(test,geometries[g],papers[p]),packed,"Preview applies C1 crop, geometry and padding once");
            }
        }
    }
    private static object[] NeutralPapers()=>new[]{ParsePaper(""),ParsePaper(JsonSerializer.Serialize(new{version=1,active=false,paddingBurn=true,paddingFraction=.7,layers=new[]{new{asset=new string('f',64),name="Missing inactive",mode="Multiply",opacity=1d}}})),
        ParsePaper(JsonSerializer.Serialize(new{version=1,active=true,paddingBurn=false,paddingFraction=.7,layers=new[]{new{asset=new string('f',64),name="Missing zero opacity",mode="Multiply",opacity=0d}}}))};
    private static void Neutral()
    {
        int index=0;
        foreach(string output in new[]{"Srgb","AdobeRgb","ProPhotoRgb","LinearRec2020"})foreach(string extension in new[]{"tif","png","jpg","dng"})
        {
            var test=Case(output);string old=Path.Combine(folder,$"neutral-{index}-old.{extension}");var original=Write(old,test,geometries[1],null);
            foreach(var paper in NeutralPapers())
            {
                string current=Path.Combine(folder,$"neutral-{index++}-new.{extension}");var result=Write(current,test,geometries[1],paper);
                Check(result==original,"Neutral paper changed writer status/dimensions");Check(File.ReadAllBytes(old).SequenceEqual(File.ReadAllBytes(current)),"Neutral paper changed "+extension+" bytes");
                EqualBitmap(Preview(test,geometries[1],paper),Preview(test,geometries[1],null),"Neutral preview");
            }
        }
    }
    private static ushort Code(double value)=>(ushort)Math.Round(Math.Clamp(value,0,1)*65535);
    private static void OtherFormats()
    {
        var test=Case("LinearRec2020",1);var expected=Pipeline(test,0,0);string dng=Path.Combine(folder,"paper-linear.dng");var info=Write(dng,test,geometries[0],papers[0]);var bytes=File.ReadAllBytes(dng);
        var tags=new Dictionary<ushort,uint>();for(int i=0,n=BitConverter.ToUInt16(bytes,8);i<n;i++){int p=10+i*12;tags[BitConverter.ToUInt16(bytes,p)]=BitConverter.ToUInt32(bytes,p+8);}
        Check(tags[256]==expected.Width&&tags[257]==expected.Height,"DNG padded dimensions");int offset=(int)tags[273];
        int exposure=(int)tags[50730];Check(BitConverter.ToInt32(bytes,exposure)==0,"Active paper deliberately clips headroom before DNG");
        for(int p=0;p<expected.Width*expected.Height;p++)for(int c=0;c<3;c++)Check(BitConverter.ToUInt16(bytes,offset+(p*3+c)*2)==Code(expected.Pixels.Span[p*4+c]),"DNG exact padded linear RGB16");
        Check(info.Detail.Contains("paper finish")&&info.Detail.Contains("headroom 0"),"DNG paper status");
        foreach(string target in new[]{"Srgb","DavinciWideGamut"})
        {
            string path=Path.Combine(folder,"paper-"+target+".jpg");var result=Write(path,Case(target),geometries[0],papers[0]);
            using var stream=File.OpenRead(path);var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
            Check(image.PixelWidth==result.Width&&image.PixelHeight==result.Height&&image.Format.BitsPerPixel==24,"JPEG paper dimensions and format");
            Check(result.Detail.Contains("JPEG quality 92"),"JPEG explicit quality retained");
        }
    }
    private static void Errors()
    {
        var missing=ParsePaper(JsonSerializer.Serialize(new{version=1,active=true,paddingBurn=true,paddingFraction=.2,layers=new[]{new{asset=new string('f',64),name="Missing",mode="Multiply",opacity=.35}}}));
        var test=Case("Srgb");string path=Path.Combine(folder,"existing.png");File.WriteAllText(path,"Do not overwrite");
        Reject<FileNotFoundException>(()=>Write(path,test,geometries[0],missing));Check(File.ReadAllText(path)=="Do not overwrite","Missing texture overwrote destination");
        Reject<FileNotFoundException>(()=>Preview(test,geometries[0],missing));
        Reject<OperationCanceledException>(()=>Write(path,test,geometries[0],papers[0],new CancellationToken(true)));Check(File.ReadAllText(path)=="Do not overwrite","Cancelled texture export overwrote destination");
        var serial=(string)Call(T("PaperDecorationStorage"),"Serialize",papers[0])!;
        Check(ReferenceEquals(papers[0],ParsePaper(serial)),"Paper capture/reload cache identity");
    }
}
