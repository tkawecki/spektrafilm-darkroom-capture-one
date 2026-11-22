using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
internal static class Program
{
    private static int checks,failures;private static double maximumError;
    private static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    private static void Reject<T>(Action action) where T:Exception {try{action();}catch(T){checks++;return;}throw new Exception("Expected "+typeof(T).Name);}
    private static void Test(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
    private static int Main()
    {
        Test("Pinned CPU geometry oracle with unbounded RGB and separate alpha",Oracle);
        Test("Neutral returns the original memory, including signed zero and inactive values",Identity);
        Test("Pixel centre mapping agrees with rendered interior coordinate ramps",Mapping);
        Test("Validation, empty crops, limits and cancellation precede publication",Invalid);
        Console.WriteLine($"{checks} assertions; {failures} failing groups; maximum CPU error {maximumError:E6}");return failures==0?0:1;
    }
    private static void Oracle()
    {
        using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));var images=data.RootElement.GetProperty("images");int index=0;double worst=0;string worstLabel="";
        foreach(var item in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var image=images[item.GetProperty("image").GetInt32()];int w=image.GetProperty("width").GetInt32(),h=image.GetProperty("height").GetInt32();
            var pixels=image.GetProperty("rgba").EnumerateArray().Select(x=>x.GetSingle()).ToArray();var before=(float[])pixels.Clone();
            var center=item.GetProperty("center");var size=item.GetProperty("size");
            var p=new ScanGeometrySettings(item.GetProperty("active").GetBoolean(),item.GetProperty("crop").GetBoolean(),center[0].GetDouble(),center[1].GetDouble(),size[0].GetDouble(),size[1].GetDouble(),item.GetProperty("skew").GetDouble(),item.GetProperty("desqueeze").GetDouble());
            int ow=item.GetProperty("width").GetInt32(),oh=item.GetProperty("height").GetInt32();
            if(ow==0||oh==0){Reject<ArgumentException>(()=>ScanGeometry.ApplyRgba(pixels,w,h,p));index++;continue;}
            var actual=ScanGeometry.ApplyRgba(pixels,w,h,p);Check(actual.Width==ow&&actual.Height==oh,"Dimensions at case "+index);
            var expected=item.GetProperty("expected").EnumerateArray().Select(x=>x.GetDouble()).ToArray();var values=actual.Pixels.Span;
            for(int i=0;i<values.Length;i++)
            {
                double error=Math.Abs(values[i]-expected[i]);maximumError=Math.Max(maximumError,error);if(error>worst){worst=error;worstLabel=$"case {index} sample {i}: actual {values[i]:R} expected {expected[i]:R}; factor {p.Desqueeze} angle {p.SkewDegrees}";}
                Check(float.IsFinite(values[i]),"Nonfinite geometry sample");
                if(i%4==3)Check(values[i]>=0&&values[i]<=1,"Alpha outside [0,1]");
            }
            Check(pixels.SequenceEqual(before),"Input mutated");index++;
        }
        Check(worst<2e-6,$"CPU oracle mismatch {worst:E6} at {worstLabel}");
    }
    private static void Identity()
    {
        var data=new float[]{float.NaN,-0f,float.PositiveInfinity,.2f,16,-.7f,1,1};var memory=new ReadOnlyMemory<float>(data);
        foreach(var settings in new[]{new ScanGeometrySettings(),new ScanGeometrySettings(Desqueeze:0),new ScanGeometrySettings(Desqueeze:-1),new ScanGeometrySettings(Active:false,Crop:true,SkewDegrees:32,Desqueeze:-.5)})
        {var result=ScanGeometry.ApplyRgba(memory,2,1,settings);Check(result.Pixels.Equals(memory),"Identity copied the source");Check(BitConverter.SingleToInt32Bits(result.Pixels.Span[1])==BitConverter.SingleToInt32Bits(-0f),"Signed zero changed");}
        var crop=new ScanGeometrySettings(Crop:true,SizeX:.5,SizeY:.5);var pixels=Enumerable.Range(0,8*6).SelectMany(i=>new float[]{i,i+1,i+2,1}).ToArray();
        var sliced=ScanGeometry.ApplyRgba(pixels,8,6,crop);Check(sliced.Width==4&&sliced.Height==4,"Crop size is not measured against the long side");
        for(int y=0;y<4;y++)for(int x=0;x<4;x++)Check(sliced.Pixels.Span[(y*4+x)*4]==pixels[((y+1)*8+x+2)*4],"Crop did not preserve exact samples");
    }
    private static void Mapping()
    {
        const int w=80,h=60;var pixels=new float[w*h*4];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=(y*w+x)*4;pixels[i]=x/(float)(w-1);pixels[i+1]=y/(float)(h-1);pixels[i+2]=.2f;pixels[i+3]=1;}
        foreach(var settings in new[]{new ScanGeometrySettings(Desqueeze:1.33,SkewDegrees:13.7,Crop:true,SizeX:.5,SizeY:.4),new ScanGeometrySettings(Desqueeze:-1.5,SkewDegrees:-18,Crop:true,SizeX:.4,SizeY:.5)})
        {
            var output=ScanGeometry.ApplyRgba(pixels,w,h,settings);int x=output.Width/2,y=output.Height/2;var point=output.Geometry.MapOutputToInput(x,y);var data=output.Pixels.Span;int i=(y*output.Width+x)*4;
            Check(Math.Abs(data[i]*(w-1)-point.X)<6e-6,"Mapped X differs from resampled ramp");Check(Math.Abs(data[i+1]*(h-1)-point.Y)<6e-6,"Mapped Y differs from resampled ramp");
        }
    }
    private static void Invalid()
    {
        var image=new float[]{0,0,0,1};var p=new ScanGeometrySettings(SkewDegrees:1);
        Reject<OperationCanceledException>(()=>ScanGeometry.ApplyRgba(image,1,1,p,new CancellationToken(true)));
        Reject<ArgumentException>(()=>ScanGeometry.ApplyRgba(Array.Empty<float>(),1,1,p));
        Reject<ArgumentException>(()=>ScanGeometry.ApplyRgba(new float[]{float.NaN,0,0,1},1,1,p));
        Reject<ArgumentException>(()=>ScanGeometry.ApplyRgba(new float[]{0,0,0,-1},1,1,p));
        Reject<ArgumentException>(()=>ScanGeometry.Plan(1,1,new(Crop:true,SizeX:.005,SizeY:.005)));
        Reject<ArgumentOutOfRangeException>(()=>ScanGeometry.Plan(8000,8000,new(Desqueeze:2)));
        Reject<ArgumentOutOfRangeException>(()=>ScanGeometry.Plan(20000,1,new()));
        Reject<ArgumentOutOfRangeException>(()=>ScanGeometry.Plan(2,2,new(SkewDegrees:double.NaN)));
        Reject<ArgumentOutOfRangeException>(()=>ScanGeometry.Plan(2,2,new(Desqueeze:2.1)));
        Reject<ArgumentOutOfRangeException>(()=>ScanGeometry.Plan(2,2,new(SizeX:0)));
    }
}
