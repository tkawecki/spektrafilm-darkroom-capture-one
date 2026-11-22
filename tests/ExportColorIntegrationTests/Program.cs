using System;
using System.Collections.Generic;
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
    private static Assembly panel=null!;
    private static Type preview=null!, frameType=null!, inputType=null!, outputType=null!;
    private static int assertions, failures;
    private const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [STAThread] private static int Main()
    {
        const string c1=@"C:\Program Files\Capture One\Capture One";
        AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists(Path.Combine(c1,n.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(c1,n.Name+".dll")):null;
        panel=Assembly.Load("NativePanel");preview=panel.GetType("SpatialPreview",true)!;frameType=panel.GetType("HostImageSource+Frame",true)!;inputType=panel.GetType("PreviewInputSpace",true)!;outputType=panel.GetType("ExportRgbSpace",true)!;
        Test("Production wide export matches Colour/SciPy pipeline across input spaces", Fixtures);
        Test("Out-of-sRGB primaries survive before destination packing", Primaries);
        Test("sRGB destination remains byte-exact with existing production export", SrgbIdentity);
        Test("Native signed gamma24 keeps negative latitude before strength/finishing", NativeGamma);
        Test("Wide export rejects missing/nonfinite pixels and honors cancellation", Errors);
        Test("Linear DNG retains HDR and negative latitude through full finishing, strength and crop", Dng);
        Console.WriteLine($"{assertions} assertions; {failures} failing groups.");return failures==0?0:1;
    }
    private static void Test(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);}}
    private static void Check(bool ok,string message){assertions++;if(!ok)throw new Exception(message);}
    private static byte[] Bytes(float[] values)=>MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static object Frame(float[] source,int w,int h)=>Activator.CreateInstance(frameType,w,h,Array.Empty<byte>(),Bytes(source),16)!;
    private static object Finish(bool active=false,double micro=0)
    {
        var values=new Dictionary<string,double>{{"finishingMicroContrast",micro}};
        if(active){values["finishingGamma"]=1.7;values["finishingContrast"]=.2;values["finishingSaturation"]=.65;}
        var settings=panel.GetType("FinishingSettings",true)!.GetMethod("FromDictionary",Static)!.Invoke(null,new object[]{values})!;
        return Activator.CreateInstance(panel.GetType("FinishingTransform",true)!,settings)!;
    }
    private static BitmapSource Export(object frame,byte[]? render,double strength,string input,string encoding,object finish,string output,PixelFormat format,Rectangle crop,CancellationToken token=default,bool legacy=false)
    {
        object?[] args=[frame,render,strength,Enum.Parse(inputType,input),encoding,format,crop,token,finish];
        if(!legacy)args=args.Append(Enum.Parse(outputType,output)).ToArray();
        return (BitmapSource)preview.GetMethod(legacy?"DecodeExportWithFinishing":"DecodeExportColorManaged",Static)!.Invoke(null,args)!;
    }
    private static ushort[] Codes(BitmapSource bitmap){int stride=bitmap.PixelWidth*bitmap.Format.BitsPerPixel/8;var bytes=new byte[stride*bitmap.PixelHeight];bitmap.CopyPixels(bytes,stride,0);return MemoryMarshal.Cast<byte,ushort>(bytes).ToArray();}
    private static ushort Code(double value)=>(ushort)Math.Round(Math.Clamp(value,0,1)*65535);
    private static void Dng()
    {
        using var data=Data();var root=data.RootElement;int w=root.GetProperty("width").GetInt32(),h=root.GetProperty("height").GetInt32();
        var source=root.GetProperty("source").EnumerateArray().Select(x=>x.GetSingle()).ToArray();var render=Bytes(root.GetProperty("linear").EnumerateArray().Select(x=>x.GetSingle()).ToArray());var frame=Frame(source,w,h);
        var method=preview.GetMethod("DecodeLinearDng",Static)!;int negative=0,hdr=0;
        foreach(var test in root.GetProperty("cases").EnumerateArray())
        {
            if(test.GetProperty("output").GetString()!="LinearRec2020")continue;
            double strength=test.GetProperty("strength").GetDouble();var finish=Finish(test.GetProperty("finishing").GetBoolean(),test.GetProperty("micro").GetDouble());var input=Enum.Parse(inputType,test.GetProperty("input").GetString()!);
            var expected=test.GetProperty("unclipped").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
            var full=(float[])method.Invoke(null,new object?[]{frame,strength==0?null:render,strength,input,"linear_rec709",new Rectangle(0,0,w,h),CancellationToken.None,finish})!;
            for(int p=0;p<w*h;p++)
            {
                for(int c=0;c<3;c++){double value=full[p*4+c];Check(Math.Abs(value-expected[p*3+c])<2e-6,"DNG unbounded Colour/SciPy mismatch");if(value<0)negative++;if(value>1)hdr++;}
                Check(BitConverter.SingleToInt32Bits(full[p*4+3])==BitConverter.SingleToInt32Bits(source[p*4+3]),"DNG source alpha changed");
            }
            var crop=new Rectangle(2,1,w-4,h-2);
            var partial=(float[])method.Invoke(null,new object?[]{frame,strength==0?null:render,strength,input,"linear_rec709",crop,CancellationToken.None,finish})!;
            for(int y=0;y<crop.Height;y++)for(int x=0;x<crop.Width;x++)for(int c=0;c<4;c++)Check(partial[(y*crop.Width+x)*4+c]==full[((y+crop.Y)*w+x+crop.X)*4+c],"DNG crop precedes full processing");
            if(strength==0 && test.GetProperty("input").GetString()=="LinearRec2020")Check(full.SequenceEqual(source),"Disabled BT.2020 source lost exact float identity");
        }
        Check(negative>0 && hdr>0,"DNG tests did not retain negative/HDR samples");
    }
    private static JsonDocument Data()=>JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));
    private static void Fixtures()
    {
        using var data=Data();var root=data.RootElement;int w=root.GetProperty("width").GetInt32(),h=root.GetProperty("height").GetInt32();
        var source=root.GetProperty("source").EnumerateArray().Select(x=>x.GetSingle()).ToArray();var render=Bytes(root.GetProperty("linear").EnumerateArray().Select(x=>x.GetSingle()).ToArray());var before=(byte[])render.Clone();var frame=Frame(source,w,h);int max=0;string worst="";
        foreach(var test in root.GetProperty("cases").EnumerateArray())
        {
            string input=test.GetProperty("input").GetString()!,output=test.GetProperty("output").GetString()!;double strength=test.GetProperty("strength").GetDouble();
            var finish=Finish(test.GetProperty("finishing").GetBoolean(),test.GetProperty("micro").GetDouble());var expected=test.GetProperty("expected").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
            foreach(var format in new[]{PixelFormats.Rgb48,PixelFormats.Rgba64})
            {
                int channels=format==PixelFormats.Rgba64?4:3;var full=Codes(Export(frame,strength==0?null:render,strength,input,"linear_rec709",finish,output,format,new(0,0,w,h)));
                for(int p=0;p<w*h;p++)for(int c=0;c<3;c++){int difference=Math.Abs(full[p*channels+c]-expected[p*3+c]);if(difference>max){max=difference;worst=$"{input}->{output}, strength {strength}, finish {test.GetProperty("finishing")}, micro {test.GetProperty("micro")}, pixel {p}/{c}";}}
                if(channels==4)for(int p=0;p<w*h;p++)Check(full[p*4+3]==Code(source[p*4+3]),"Alpha came from rendered/finishing data");
                var crop=new Rectangle(2,1,w-4,h-2);var partial=Codes(Export(frame,strength==0?null:render,strength,input,"linear_rec709",finish,output,format,crop));
                for(int y=0;y<crop.Height;y++)for(int x=0;x<crop.Width;x++)for(int c=0;c<channels;c++)Check(partial[(y*crop.Width+x)*channels+c]==full[((y+crop.Y)*w+x+crop.X)*channels+c],"Crop preceded full-image micro processing");
                Check(full.Where((_,i)=>i%channels<3).Any(x=>x%257!=0),"Output collapsed to 8-bit");
            }
        }
        Check(render.SequenceEqual(before),"Rendered float buffer modified");
        Check(max<=1,$"Independent output differs by {max} codes at {worst}");
    }
    private static void Primaries()
    {
        using var data=Data();
        foreach(var item in data.RootElement.GetProperty("primaries").EnumerateArray())
        {
            var rgb=item.GetProperty("linear").EnumerateArray().Select(x=>x.GetSingle()).Append(1f).ToArray();var frame=Frame(new float[]{.2f,.3f,.4f,.7f},1,1);
            var actual=Codes(Export(frame,Bytes(rgb),1,"Srgb","linear_rec709",Finish(),item.GetProperty("space").GetString()!,PixelFormats.Rgba64,new(0,0,1,1)));
            for(int c=0;c<3;c++)
            {
                Check(Math.Abs(actual[c]-item.GetProperty("expected")[c].GetInt32())<=1,"Float32 wide primary differs from independent Colour conversion");
                Check(Math.Abs(actual[c]-Code(item.GetProperty("target")[c].GetDouble()))<=32,"Wide primary lost before destination conversion");
            }
            Check(actual[3]==Code(.7f),"Wide primary alpha changed");
        }
    }
    private static void SrgbIdentity()
    {
        using var data=Data();int w=data.RootElement.GetProperty("width").GetInt32(),h=data.RootElement.GetProperty("height").GetInt32();var source=data.RootElement.GetProperty("source").EnumerateArray().Select(x=>x.GetSingle()).ToArray();var render=Bytes(data.RootElement.GetProperty("linear").EnumerateArray().Select(x=>x.GetSingle()).ToArray());var frame=Frame(source,w,h);
        foreach(var input in new[]{"Srgb","AdobeRgb","ProPhotoRgb","LinearRec2020"})foreach(var strength in new[]{0d,.27,1d})foreach(var finish in new[]{Finish(),Finish(true),Finish(true,.4)})foreach(var format in new[]{PixelFormats.Rgb48,PixelFormats.Rgba64})
        {
            var crop=new Rectangle(1,1,w-2,h-2);var actual=Codes(Export(frame,strength==0?null:render,strength,input,"linear_rec709",finish,"Srgb",format,crop));var previous=Codes(Export(frame,strength==0?null:render,strength,input,"linear_rec709",finish,"Srgb",format,crop,legacy:true));
            Check(actual.SequenceEqual(previous),"sRGB destination changed existing export bytes");
        }
    }
    private static void NativeGamma()
    {
        string encoding=(string)preview.GetMethod("ResultEncoding",Static)!.Invoke(null,new object[]{"rec709_gamma24"})!;
        var linear=new float[]{-.2f,1.8f,.3f,1};var encoded=linear.Select((v,i)=>i==3?v:(float)Math.CopySign(Math.Pow(Math.Abs(v),1/2.4),v)).ToArray();var frame=Frame(new float[]{.9f,.2f,.7f,.6f},1,1);
        foreach(var finish in new[]{Finish(),Finish(true)})foreach(var destination in new[]{"Srgb","AdobeRgb","ProPhotoRgb"})
        {
            var expected=Codes(Export(frame,Bytes(linear),.25,"Srgb","linear_rec709",finish,destination,PixelFormats.Rgba64,new(0,0,1,1)));
            var actual=Codes(Export(frame,Bytes(encoded),.25,"Srgb",encoding,finish,destination,PixelFormats.Rgba64,new(0,0,1,1)));
            for(int c=0;c<4;c++)Check(Math.Abs(actual[c]-expected[c])<=1,"Native signed gamma24 discarded latitude: "+destination+" channel "+c);
        }
    }
    private static void Errors()
    {
        var frame=Frame(new float[]{.2f,.3f,.4f,1},1,1);
        void Throws(Action action,string label){try{action();}catch(Exception){assertions++;return;}throw new Exception(label);}
        Throws(()=>Export(frame,null,1,"Srgb","linear_rec709",Finish(),"AdobeRgb",PixelFormats.Rgb48,new(0,0,1,1)),"Missing pixels accepted");
        Throws(()=>Export(frame,Bytes(new float[]{float.NaN,.3f,.4f,1}),1,"Srgb","linear_rec709",Finish(),"AdobeRgb",PixelFormats.Rgb48,new(0,0,1,1)),"Nonfinite rendered pixels accepted");
        Throws(()=>Export(frame,Bytes(new float[]{.2f,.3f,.4f,1}),1,"Srgb","linear_rec709",Finish(),"AdobeRgb",PixelFormats.Rgb48,new(0,0,1,1),new CancellationToken(true)),"Cancellation ignored");
    }
}
