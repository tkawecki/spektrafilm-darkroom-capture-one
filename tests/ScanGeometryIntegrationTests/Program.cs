using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static Assembly panel=null!;
    private static Type preview=null!,frameType=null!,inputType=null!,outputType=null!,geometryType=null!,settingsType=null!;
    private static int checks,failures,negative,hdr;
    private static double maxFinishError,maxGeometryError,largeSeconds;
    private static long largePeakWorkingSet;
    private static readonly string artifacts=Path.GetFullPath(Path.Combine("artifacts/full-function-20261116/scan-geometry-integration",Guid.NewGuid().ToString("N")));
    [STAThread] private static int Main(string[] args)
    {
        const string c1=@"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
        panel=Assembly.Load("NativePanel");preview=panel.GetType("SpatialPreview",true)!;frameType=panel.GetType("HostImageSource+Frame",true)!;
        inputType=panel.GetType("PreviewInputSpace",true)!;outputType=panel.GetType("ExportRgbSpace",true)!;
        geometryType=panel.GetType("ScanGeometry",true)!;settingsType=panel.GetType("ScanGeometrySettings",true)!;
        Directory.CreateDirectory(artifacts);
        Test("Production float, full-frame micro, C1 crop, seventeen target pairs and reference geometry oracle",Fixtures);
        Test("Neutral geometry delegates byte-exact TIFF/PNG/JPEG/DNG writers",NeutralFiles);
        Test("Geometry writer preserves RGB16/alpha/ICC and linear DNG latitude",WrittenFiles);
        Test("Cancellation and malformed frames fail before output writes",Errors);
        if(!args.Contains("--skip-large"))Test("48 MP production float and desqueeze/skew/crop path",Large);
        File.WriteAllText(Path.Combine(artifacts,"verification.json"),JsonSerializer.Serialize(new{checks,failures,maxFinishError,maxGeometryError,negative,hdr,largeSeconds,largePeakWorkingSet,
            largeRun=!args.Contains("--skip-large"),panelSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(panel.Location)))},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{checks} assertions; {failures} failing groups; max float {maxFinishError:E6}, geometry {maxGeometryError:E6}; artifacts {artifacts}");
        return failures==0?0:1;
    }
    private static void Test(string name,Action run){try{run();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException());}}
    private static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    private static void Reject<T>(Action action) where T:Exception
    {try{action();}catch(Exception e)when(e.GetBaseException() is T){checks++;return;}throw new Exception("Expected "+typeof(T).Name);}
    private static object Call(Type type,string method,params object?[] args)=>type.GetMethod(method,Static)!.Invoke(null,args)!;
    private static object Prop(object value,string name)=>value.GetType().GetProperty(name,Instance)!.GetValue(value)!;
    private static byte[] Bytes(float[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static object Frame(byte[] source,int w,int h)=>Activator.CreateInstance(frameType,w,h,Array.Empty<byte>(),source,16)!;
    private static object Finish(bool active=false,double micro=0)
    {
        var values=new Dictionary<string,double>{{"finishingMicroContrast",micro}};
        if(active){values["finishingGamma"]=1.7;values["finishingContrast"]=.2;values["finishingSaturation"]=.65;}
        return Activator.CreateInstance(panel.GetType("FinishingTransform",true)!,Call(panel.GetType("FinishingSettings",true)!,"FromDictionary",values))!;
    }
    private static object Geometry(bool active=true,bool crop=false,double cx=.5,double cy=.5,double sx=1,double sy=1,double angle=0,double factor=1)
        =>Activator.CreateInstance(settingsType,active,crop,cx,cy,sx,sy,angle,factor)!;
    private static object Geometry(JsonElement p)=>Geometry(p.GetProperty("active").GetBoolean(),p.GetProperty("crop").GetBoolean(),
        p.GetProperty("crop_center")[0].GetDouble(),p.GetProperty("crop_center")[1].GetDouble(),p.GetProperty("crop_size")[0].GetDouble(),p.GetProperty("crop_size")[1].GetDouble(),
        p.GetProperty("skew_deg").GetDouble(),p.GetProperty("desqueeze").GetDouble());
    private static float[] Decode(object frame,byte[]? render,double strength,string input,string output,Rectangle crop,object finish,CancellationToken token=default)
        =>(float[])Call(preview,"DecodeFinishedFloat",frame,render,strength,Enum.Parse(inputType,input),"linear_rec709",crop,token,finish,Enum.Parse(outputType,output));
    private static (ReadOnlyMemory<float> Pixels,int Width,int Height) Apply(float[] pixels,int w,int h,object settings,CancellationToken token=default)
    {var result=Call(geometryType,"ApplyRgba",new ReadOnlyMemory<float>(pixels),w,h,settings,token);return((ReadOnlyMemory<float>)Prop(result,"Pixels"),(int)Prop(result,"Width"),(int)Prop(result,"Height"));}
    private static (int Width,int Height,string Detail) Write(string path,object frame,byte[]? render,double strength,string space,Rectangle crop,object finish,object? geometry)
    {
        var color=Enum.Parse(outputType,space);string profile=Profile(space);
        object?[] args=[path,frame,render,strength,Enum.Parse(inputType,space),"linear_rec709",crop,CancellationToken.None,finish,color,profile,92];
        if(geometry is not null)args=args.Append(geometry).ToArray();
        return ((int,int,string))Call(preview,geometry is null?"WriteExportImage":"WriteGeometryExportImage",args);
    }
    private static string Profile(string space)=>(string)Call(panel.GetType("ExportColorSpace",true)!,"ResolveProfile",Enum.Parse(outputType,space),Path.GetFullPath("."),null);
    private static JsonDocument Data()=>JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));
    private static float[] Floats(JsonElement array)=>array.EnumerateArray().Select(x=>x.GetSingle()).ToArray();
    private static Rectangle Crop(JsonElement data){var v=data.GetProperty("crop");return new(v[0].GetInt32(),v[1].GetInt32(),v[2].GetInt32(),v[3].GetInt32());}
    private static void Fixtures()
    {
        using var json=Data();var data=json.RootElement;int w=data.GetProperty("width").GetInt32(),h=data.GetProperty("height").GetInt32();
        var source=Bytes(Floats(data.GetProperty("source")));var render=Bytes(Floats(data.GetProperty("linear")));var sourceBefore=(byte[])source.Clone();var renderBefore=(byte[])render.Clone();
        var frame=Frame(source,w,h);var crop=Crop(data);var geometries=data.GetProperty("parameters").EnumerateArray().Select(Geometry).ToArray();
        foreach(var test in data.GetProperty("cases").EnumerateArray())
        {
            string input=test.GetProperty("input").GetString()!,output=test.GetProperty("output").GetString()!;double strength=test.GetProperty("strength").GetDouble();
            var finish=Finish(test.GetProperty("finishing").GetBoolean(),test.GetProperty("micro").GetDouble());var rendered=strength==0?null:render;
            var full=Decode(frame,rendered,strength,input,output,new(0,0,w,h),finish);
            var partial=Decode(frame,rendered,strength,input,output,crop,finish);var expected=Floats(test.GetProperty("finished"));
            for(int y=0;y<crop.Height;y++)for(int x=0;x<crop.Width;x++)for(int c=0;c<4;c++)
            {
                int i=(y*crop.Width+x)*4+c;Check(partial[i]==full[((y+crop.Top)*w+x+crop.Left)*4+c],"C1 crop preceded full-frame finishing/micro");
                maxFinishError=Math.Max(maxFinishError,Math.Abs(partial[i]-expected[i]));
                if(c<3){if(partial[i]<0)negative++;if(partial[i]>1)hdr++;}
            }
            if(strength==0&&input==output)Check(Bytes(full).SequenceEqual(source),"Disabled matching target lost source identity");
            for(int g=0;g<geometries.Length;g++)
            {
                var transformed=Apply(partial,crop.Width,crop.Height,geometries[g]);var reference=test.GetProperty("results")[g];
                Check(transformed.Width==reference.GetProperty("width").GetInt32()&&transformed.Height==reference.GetProperty("height").GetInt32(),"Geometry dimensions differ from reference");
                var expectedGeometry=reference.GetProperty("rgba");
                for(int i=0;i<transformed.Pixels.Length;i++)
                {
                    maxGeometryError=Math.Max(maxGeometryError,Math.Abs(transformed.Pixels.Span[i]-expectedGeometry[i].GetDouble()));
                    if((i&3)==3)Check(transformed.Pixels.Span[i]>=0&&transformed.Pixels.Span[i]<=1,"Alpha is not bounded coverage");
                }
            }
        }
        Check(maxFinishError<=2e-6,$"Independent finishing error {maxFinishError}");Check(maxGeometryError<=3e-6,$"Independent geometry error {maxGeometryError}");
        Check(negative>0&&hdr>0,"Pre-geometry samples lost negative/HDR latitude");Check(source.SequenceEqual(sourceBefore)&&render.SequenceEqual(renderBefore),"Input buffers were modified");
    }
    private static void NeutralFiles()
    {
        using var json=Data();var data=json.RootElement;int w=data.GetProperty("width").GetInt32(),h=data.GetProperty("height").GetInt32();
        var frame=Frame(Bytes(Floats(data.GetProperty("source"))),w,h);var render=Bytes(Floats(data.GetProperty("linear")));var crop=Crop(data);int id=0;
        foreach(string space in new[]{"Srgb","AdobeRgb","ProPhotoRgb","LinearRec2020"})foreach(string extension in new[]{"tif","png","jpg","dng"})foreach(double strength in new[]{0d,.27,1d})
        {
            var finish=Finish(true,.4);var old=Path.Combine(artifacts,$"neutral-{id}-old.{extension}");var previous=Write(old,frame,strength==0?null:render,strength,space,crop,finish,null);
            foreach(var geometry in new[]{Geometry(),Geometry(active:false,crop:true,angle:25,factor:1.5),Geometry(factor:-1)})
            {
                var path=Path.Combine(artifacts,$"neutral-{id++}-new.{extension}");var actual=Write(path,frame,strength==0?null:render,strength,space,crop,finish,geometry);
                Check(actual==previous,"Neutral changed output dimensions/status");Check(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(old)),"Neutral changed "+extension+" file bytes");
            }
        }
    }
    private static void WrittenFiles()
    {
        using var json=Data();var data=json.RootElement;int w=data.GetProperty("width").GetInt32(),h=data.GetProperty("height").GetInt32();
        var frame=Frame(Bytes(Floats(data.GetProperty("source"))),w,h);var render=Bytes(Floats(data.GetProperty("linear")));var crop=Crop(data);var geometry=Geometry(data.GetProperty("parameters")[0]);
        foreach(string space in new[]{"Srgb","AdobeRgb","ProPhotoRgb","LinearRec2020"})
        {
            var finish=Finish(true,.4);var expected=Apply(Decode(frame,render,.27,space,space,crop,finish),crop.Width,crop.Height,geometry);
            foreach(string extension in new[]{"tif","png","jpg"})
            {
                string path=Path.Combine(artifacts,$"geometry-{space}.{extension}");var info=Write(path,frame,render,.27,space,crop,finish,geometry);
                using var stream=File.OpenRead(path);var image=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                Check(image.PixelWidth==expected.Width&&image.PixelHeight==expected.Height&&info.Width==expected.Width&&info.Height==expected.Height,"Writer dimensions");
                using var icc=image.ColorContexts![0].OpenProfileStream();using var memory=new MemoryStream();icc.CopyTo(memory);Check(memory.ToArray().SequenceEqual(File.ReadAllBytes(Profile(space))),"Writer ICC changed");
                if(extension=="jpg")continue;
                int channels=extension=="png"?4:3;var codes=new ushort[expected.Width*expected.Height*channels];image.CopyPixels(codes,expected.Width*channels*2,0);
                Check(image.Format==(extension=="png"?PixelFormats.Rgba64:PixelFormats.Rgb48),"Writer reduced precision");
                for(int p=0;p<expected.Width*expected.Height;p++)for(int c=0;c<channels;c++)Check(codes[p*channels+c]==Code(expected.Pixels.Span[p*4+c]),"Writer changed finished float packing or alpha");
                Check(codes.Any(c=>c%257!=0),"RGB16 became 8-bit");
            }
        }
        var dngFinish=Finish(true,.4);var dngExpected=Apply(Decode(frame,render,1,"Srgb","LinearRec2020",crop,dngFinish),crop.Width,crop.Height,geometry);
        string dng=Path.Combine(artifacts,"geometry-linear.dng");var dngInfo=Write(dng,frame,render,1,"Srgb",crop,dngFinish,geometry);var bytes=File.ReadAllBytes(dng);
        var tags=new Dictionary<ushort,uint>();int count=BitConverter.ToUInt16(bytes,8);for(int i=0;i<count;i++){int p=10+i*12;tags[BitConverter.ToUInt16(bytes,p)]=BitConverter.ToUInt32(bytes,p+8);}
        Check(tags[256]==dngExpected.Width&&tags[257]==dngExpected.Height,"DNG dimensions");int exposure=(int)tags[50730];int stops=BitConverter.ToInt32(bytes,exposure)/BitConverter.ToInt32(bytes,exposure+4);
        Check(stops>0&&dngInfo.Detail.Contains("headroom"),"DNG lost HDR headroom/reporting");int offset=(int)tags[273];
        for(int p=0;p<dngExpected.Width*dngExpected.Height;p++)for(int c=0;c<3;c++)Check(BitConverter.ToUInt16(bytes,offset+(p*3+c)*2)==Code(dngExpected.Pixels.Span[p*4+c]/(double)(1<<stops)),"DNG prematurely clipped or changed space");
    }
    private static ushort Code(double value)=>(ushort)Math.Round(Math.Clamp(value,0,1)*65535);
    private static void Errors()
    {
        var frame=Frame(Bytes(new float[]{.2f,.4f,.6f,.8f}),1,1);var finish=Finish();var rect=new Rectangle(0,0,1,1);var render=Bytes(new float[]{-.2f,1.8f,.3f,0});
        Reject<OperationCanceledException>(()=>Decode(frame,render,1,"Srgb","Srgb",rect,finish,new(true)));
        Reject<InvalidDataException>(()=>Decode(frame,null,1,"Srgb","Srgb",rect,finish));
        Reject<InvalidDataException>(()=>Decode(frame,render,1,"Srgb","Srgb",new(0,0,2,1),finish));
        Reject<ArgumentOutOfRangeException>(()=>Decode(frame,render,double.NaN,"Srgb","Srgb",rect,finish));
        Reject<ArgumentException>(()=>Decode(frame,Bytes(new float[]{float.NaN,0,0,1}),1,"Srgb","Srgb",rect,finish));
        var path=Path.Combine(artifacts,"cancelled.png");File.WriteAllText(path,"original");
        Reject<OperationCanceledException>(()=>Call(preview,"WriteGeometryExportImage",path,frame,render,1d,Enum.Parse(inputType,"Srgb"),"linear_rec709",rect,
            new CancellationToken(true),finish,Enum.Parse(outputType,"Srgb"),Profile("Srgb"),92,Geometry(angle:1)));
        Check(File.ReadAllText(path)=="original","Cancelled export modified an existing destination");
    }
    private static void Large()
    {
        const int w=8000,h=6000;GC.Collect();GC.WaitForPendingFinalizers();
        var watch=Stopwatch.StartNew();var source=new byte[w*h*16];var values=MemoryMarshal.Cast<byte,float>(source.AsSpan());
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=(y*w+x)*4;values[i]=-.1f+x/(float)w*1.4f;values[i+1]=.2f;values[i+2]=1.7f;values[i+3]=.4f;}
        var frame=Frame(source,w,h);var finish=Finish();var finished=Decode(frame,source,.27,"LinearRec2020","LinearRec2020",new(0,0,w,h),finish);
        Check(finished.Length==w*h*4,"48 MP resized before geometry");var geometry=Geometry(crop:true,sx:.05,sy:.05,angle:2,factor:.5);
        var result=Apply(finished,w,h,geometry);Check(result.Width==300&&result.Height==300,"48 MP final geometry dimensions");
        var central=Decode(frame,source,.27,"LinearRec2020","LinearRec2020",new(w/2,h/2,1,1),finish);
        Check(Math.Abs(result.Pixels.Span[(150*300+150)*4+2]-central[2])<2e-4,"48 MP HDR interpolation");
        Check(result.Pixels.Span[(150*300+150)*4+2]>1,"48 MP HDR clipped");Check(Math.Abs(result.Pixels.Span[3]-.4)<1e-6,"48 MP alpha changed");
        watch.Stop();largeSeconds=watch.Elapsed.TotalSeconds;largePeakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine($"48 MP: {largeSeconds:F3}s, peak working set {largePeakWorkingSet/1048576d:F1} MiB, {result.Width}x{result.Height}; synthetic only");
    }
}
