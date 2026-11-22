using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
internal static class Program
{
    private static int checks,failures;private static double maximumError,maximumIccError;
    private static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    private static void Test(string label,Action action){try{action();Console.WriteLine("PASS "+label);}catch(Exception e){failures++;Console.WriteLine("FAIL "+label+": "+e.Message);}}
    private static int Main()
    {
        Test("17 unique pairs, preserved legacy identities and honest ICC availability",Registry);
        Test("Historical four-space assembly comparison is bit-exact",Legacy);
        Test("Unbounded matrix and transfer functions match Colour 0.4.7",Oracle);
        Test("Generated ICC matrix/TRC matches independent Colour PCS and remains monotonic",Profiles);
        Test("Log highlights and negative values survive conversion until final packing",Latitude);
        Console.WriteLine($"{checks} checks; {failures} failures; max relative conversion {maximumError:E6}, ICC PCS {maximumIccError:E6}");return failures==0?0:1;
    }
    private static void Legacy()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"legacy-bit-patterns.json")));
        foreach(var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input=item.GetProperty("input");double r=input[0].GetDouble(),g=input[1].GetDouble(),b=input[2].GetDouble();var space=Enum.Parse<ExportRgbSpace>(item.GetProperty("space").GetString()!);
            var rgb=item.GetProperty("method").GetString() switch {
                "FromSrgb"=>ExportColorSpace.FromSrgb(r,g,b,space),"FromLinearSrgb"=>ExportColorSpace.FromLinearSrgb(r,g,b,space),
                "ToSrgb"=>ExportColorSpace.ToSrgb(r,g,b,space),"ToLinearSrgb"=>ExportColorSpace.ToLinearSrgb(r,g,b,space),_=>throw new Exception("Unknown baseline method")};
            double[] values=[rgb.Red,rgb.Green,rgb.Blue];for(int c=0;c<3;c++)Check(BitConverter.DoubleToInt64Bits(values[c])==item.GetProperty("bits")[c].GetInt64(),"Legacy output bits changed");
        }
    }
    private static void Registry()
    {
        Check(Enum.GetValues<ExportRgbSpace>().Length==17&&ExportColorSpace.Choices.Count==17,"17 pairs missing");
        foreach(var item in ExportColorSpace.Choices)
        {
            Check(ExportColorSpace.Parse(item.Id)==item.Space,"ID does not roundtrip");Check(ExportColorSpace.Describe(item.Space)==item.Label,"Label mismatch");
            Check(!string.IsNullOrWhiteSpace(ExportColorSpace.GamutId(item.Space))&&!string.IsNullOrWhiteSpace(ExportColorSpace.TransferId(item.Space)),"Missing metadata identity");
            bool log=item.Space is ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.VGamut;
            Check((ExportColorSpace.ResolveProfileOrNull(item.Space,Path.GetFullPath(".")) is null)==log,"Incorrect ICC availability");
        }
        string[] oldIds=["srgb","adobe_rgb","prophoto","linear_rec2020"];
        for(int i=0;i<4;i++)Check((int)ExportColorSpace.Parse(oldIds[i])==i,"Legacy numeric ID changed");
        foreach(double value in new[]{-0d,-.2,0,.3,1,2})Check(BitConverter.DoubleToInt64Bits(ExportColorSpace.FromSrgb(value,value,value,ExportRgbSpace.Srgb).Red)==BitConverter.DoubleToInt64Bits(value),"sRGB identity changed");
    }
    private static void Near(ExportColorSpace.Rgb rgb,JsonElement expected,string label,JsonElement test=default,string method="")
    {
        double[] actual=[rgb.Red,rgb.Green,rgb.Blue];
        for(int c=0;c<3;c++)
        {
            double e=expected[c].GetDouble(),difference=Math.Abs(actual[c]-e)/Math.Max(1,Math.Abs(e));
            if(test.ValueKind==JsonValueKind.Object&&test.TryGetProperty("boundary",out var boundary)&&boundary.TryGetProperty(method+c,out var limits))
                difference=Math.Min(Math.Abs(actual[c]-limits[0].GetDouble()),Math.Abs(actual[c]-limits[1].GetDouble()));
            maximumError=Math.Max(maximumError,difference);Check(difference<2e-7,$"{label}/{c}: {actual[c]} vs {e}, error {difference}");
        }
    }
    private static JsonDocument Data()=>JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));
    private static void Oracle()
    {
        using var json=Data();
        foreach(var test in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var space=Enum.Parse<ExportRgbSpace>(test.GetProperty("space").GetString()!);var input=test.GetProperty("input");var encoded=test.GetProperty("encodedInput");
            double r=input[0].GetDouble(),g=input[1].GetDouble(),b=input[2].GetDouble();
            Near(ExportColorSpace.FromLinearSrgb(r,g,b,space),test.GetProperty("fromLinear"),space+" from linear",test,"fromLinear");
            Near(ExportColorSpace.FromSrgb(r,g,b,space),test.GetProperty("fromSrgb"),space+" from sRGB",test,"fromSrgb");
            r=encoded[0].GetDouble();g=encoded[1].GetDouble();b=encoded[2].GetDouble();
            Near(ExportColorSpace.ToLinearSrgb(r,g,b,space),test.GetProperty("toLinear"),space+" to linear");
            Near(ExportColorSpace.ToSrgb(r,g,b,space),test.GetProperty("toSrgb"),space+" to sRGB");
        }
    }
    private sealed class Icc(byte[] bytes)
    {
        private int U32(int i)=>(int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i,4));
        private double Fixed(int i)=>BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i,4))/65536d;
        private int Tag(string key){for(int i=0;i<U32(128);i++)if(Encoding.ASCII.GetString(bytes,132+i*12,4)==key)return U32(136+i*12);throw new Exception("Missing ICC "+key);}
        internal double Curve(double x,string channel="r")
        {
            int i=Tag(channel+"TRC");string type=Encoding.ASCII.GetString(bytes,i,4);
            if(type=="curv")
            {
                int count=U32(i+8);if(count==0)return x;if(count==1)return Math.Pow(x,BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i+12,2))/256d);
                double at=x*(count-1);int lo=Math.Min(count-2,(int)at);double t=at-lo;
                double a=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i+12+lo*2,2))/65535d,b=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i+14+lo*2,2))/65535d;return a+(b-a)*t;
            }
            Check(type=="para","Unexpected curve type");int function=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i+8,2));
            if(function==0)return Math.Pow(x,Fixed(i+12));
            Check(function==3,"Unexpected parametric function");return x<Fixed(i+28)?Fixed(i+24)*x:Math.Pow(Fixed(i+16)*x+Fixed(i+20),Fixed(i+12));
        }
        internal double[] Xyz(double[] rgb)
        {
            string[] names=["r","g","b"];double[] result=new double[3];
            for(int c=0;c<3;c++){int at=Tag(names[c]+"XYZ");double linear=Curve(rgb[c],names[c]);for(int row=0;row<3;row++)result[row]+=linear*Fixed(at+8+row*4);}return result;
        }
    }
    private static void Profiles()
    {
        using var json=Data();
        foreach(var test in json.RootElement.GetProperty("profiles").EnumerateArray())
        {
            var space=Enum.Parse<ExportRgbSpace>(test.GetProperty("space").GetString()!);var icc=new Icc(File.ReadAllBytes(ExportColorSpace.ResolveProfile(space,Path.GetFullPath("."))));
            foreach(var point in test.GetProperty("points").EnumerateArray())
            {
                var actual=icc.Xyz(point.GetProperty("rgb").EnumerateArray().Select(x=>x.GetDouble()).ToArray());
                for(int c=0;c<3;c++){double error=Math.Abs(actual[c]-point.GetProperty("xyz")[c].GetDouble());maximumIccError=Math.Max(maximumIccError,error);Check(error<.0001,$"{space} ICC PCS error {error}");}
            }
            double previous=icc.Curve(0);for(int i=1;i<=65535;i++){double next=icc.Curve(i/65535d);Check(next>=previous,$"{space} ICC curve decreases at {i}");previous=next;}
            Check(Math.Abs(icc.Curve(0))<1e-12&&Math.Abs(icc.Curve(1)-1)<.0001,"ICC black/white endpoints");
        }
    }
    private static void Latitude()
    {
        foreach(var space in new[]{ExportRgbSpace.DavinciWideGamut,ExportRgbSpace.VGamut})foreach(double value in new[]{-.01,0,.18,1,4,16})
        {
            var encoded=ExportColorSpace.FromLinearSrgb(value,value,value,space);var restored=ExportColorSpace.ToLinearSrgb(encoded.Red,encoded.Green,encoded.Blue,space);
            Check(Math.Abs(restored.Red-value)<2e-6,"Log latitude lost");if(value is >=1 and <=16)Check(encoded.Red<1,"Log failed to preserve HDR inside integer range");
        }
        var negative=ExportColorSpace.FromLinearSrgb(-.1,-.1,-.1,ExportRgbSpace.DisplayP3);Check(negative.Red<0,"New SDR pair clips early");
        var linear=ExportColorSpace.FromLinearSrgb(2,2,2,ExportRgbSpace.LinearDavinciWideGamut);Check(linear.Red>1,"New linear pair clips early");
    }
}
