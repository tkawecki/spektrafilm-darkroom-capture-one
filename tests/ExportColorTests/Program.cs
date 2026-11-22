using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Reflection;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int assertions, failures;
    private static string repo = "", output = "";
    private static double maximumError;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    { try { action(); Console.WriteLine("PASS " + name); } catch(Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetBaseException().Message); } }
    private static void Reject<T>(Action action) where T:Exception
    { try {action();} catch(T){assertions++; return;} throw new Exception("Expected " + typeof(T).Name); }
    [STAThread] private static int Main()
    {
        repo=Path.GetFullPath(".");output=Path.Combine(repo,"artifacts","full-function-20261116","export-color-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Test("Unclipped conversion matches independent Colour fixtures in four spaces", Reference);
        Test("Wide-primary recovery proves transform precedes source gamut clipping", WidePrimaries);
        Test("Neutrality, identity, transfer boundaries and inverse unbounded conversion", Identity);
        Test("Invalid values and profile metadata reject before save", Invalid);
        Test("RGB16/ICC TIFF and PNG roundtrip, profile rejection and legacy equivalence", ExportRoundtrip);
        Test("ICC matrix/TRC match independent Colour PCS; Windows CMM diagnostic", ProfileAgreement);
        Console.WriteLine($"{assertions} assertions; {failures} failing groups; max reference error {maximumError:E3}; artifacts {output}");
        return failures==0?0:1;
    }
    private static void Near(ExportColorSpace.Rgb actual, JsonElement expected, string label)
    {
        var e=expected.EnumerateArray().Select(x=>x.GetDouble()).ToArray(); var a=new[]{actual.Red,actual.Green,actual.Blue};
        // Tiny positive roundoff near a zero Adobe channel is amplified by its
        // fractional power. 2e-7 is 0.014 of one 16-bit code, still far below
        // any quantization boundary. Colour also picks the mathematically
        // continuous sRGB threshold instead of the rounded IEC 0.04045.
        for(int c=0;c<3;c++) {double error=Math.Abs(e[c]-a[c]);maximumError=Math.Max(maximumError,error);Check(error<2e-7*Math.Max(1,Math.Abs(e[c])),label+" differs: "+error);}
    }
    private static void Reference()
    {
        using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));
        foreach(var test in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var p=test.GetProperty("input");double r=p[0].GetDouble(),g=p[1].GetDouble(),b=p[2].GetDouble();var space=Enum.Parse<ExportRgbSpace>(test.GetProperty("space").GetString()!);
            Near(ExportColorSpace.FromSrgb(r,g,b,space),test.GetProperty("output"),space+" from sRGB");
            Near(ExportColorSpace.ToSrgb(r,g,b,space),test.GetProperty("reverse"),space+" to sRGB");
            Near(ExportColorSpace.FromLinearSrgb(r,g,b,space),test.GetProperty("fromLinear"),space+" from linear");
            Near(ExportColorSpace.ToLinearSrgb(r,g,b,space),test.GetProperty("toLinear"),space+" to linear");
        }
    }
    private static void WidePrimaries()
    {
        using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json"))); int rescued=0;
        foreach(var test in data.RootElement.GetProperty("widePrimaries").EnumerateArray())
        {
            var p=test.GetProperty("srgb");double r=p[0].GetDouble(),g=p[1].GetDouble(),b=p[2].GetDouble();var space=Enum.Parse<ExportRgbSpace>(test.GetProperty("space").GetString()!);
            var recovered=ExportColorSpace.FromSrgb(r,g,b,space); Near(recovered,test.GetProperty("target"),"Wide-primary recovery");
            var clipped=ExportColorSpace.FromSrgb(Math.Clamp(r,0,1),Math.Clamp(g,0,1),Math.Clamp(b,0,1),space);
            if(Math.Abs(clipped.Red-recovered.Red)+Math.Abs(clipped.Green-recovered.Green)+Math.Abs(clipped.Blue-recovered.Blue)>.01) rescued++;
        }
        Check(rescued>=8,"Fixtures do not expose pre-transform clipping loss");
    }
    private static void Identity()
    {
        foreach(double x in new[]{-2,-.5,-.001,0,.001,.0031308,.04045,.18,.5,1,2,8})
        {
            var identity=ExportColorSpace.FromSrgb(x,x,x,ExportRgbSpace.Srgb);
            Check(BitConverter.DoubleToInt64Bits(identity.Red)==BitConverter.DoubleToInt64Bits(x),"sRGB identity changed bits");
            foreach(var space in Enum.GetValues<ExportRgbSpace>())
            {
                var color=ExportColorSpace.FromSrgb(x,x*.7,x*.1,space);var round=ExportColorSpace.ToSrgb(color.Red,color.Green,color.Blue,space);
                Check(Math.Abs(round.Red-x)<2e-6 && Math.Abs(round.Green-x*.7)<2e-6 && Math.Abs(round.Blue-x*.1)<2e-6,"Unbounded inverse differs");
                var neutral=ExportColorSpace.FromLinearSrgb(x,x,x,space);
                // New matrix spaces can land on opposite sides of sRGB's
                // rounded toe by one double ULP (2.9e-8 encoded difference).
                double neutralTolerance=(int)space<4?1e-10:2e-7;
                Check(Math.Abs(neutral.Red-neutral.Green)<neutralTolerance && Math.Abs(neutral.Red-neutral.Blue)<neutralTolerance,$"Chromatic adaptation tinted neutral: {space} {x}: {neutral}");
            }
        }
        Check(ExportColorSpace.DecodeSrgb(-.5)==-.5/12.92,"Regular C# negative convention changed");
    }
    private static void Invalid()
    {
        Reject<ArgumentException>(()=>ExportColorSpace.Parse("monitor"));
        Reject<ArgumentException>(()=>ExportColorSpace.FromSrgb(double.NaN,0,0,ExportRgbSpace.Srgb));
        Reject<ArgumentException>(()=>ExportColorSpace.FromSrgb(0,double.PositiveInfinity,0,ExportRgbSpace.ProPhotoRgb));
        string fake=Path.Combine(output,"invalid.icc");File.WriteAllBytes(fake,new byte[132]);
        Reject<InvalidDataException>(()=>ExportColorSpace.ValidateRgbProfile(fake));
    }
    private static byte[] Copy(BitmapSource image)
    {
        int stride=(image.PixelWidth*image.Format.BitsPerPixel+7)/8;var bytes=new byte[stride*image.PixelHeight];image.CopyPixels(bytes,stride,0);return bytes;
    }
    private static void ExportRoundtrip()
    {
        var method=typeof(SpektraExport).GetMethod("SaveWithProfile",BindingFlags.NonPublic|BindingFlags.Static,null,
            new[]{typeof(string),typeof(BitmapSource),typeof(CancellationToken),typeof(string)},null);
        Check(method is not null,"Explicit output ICC Save overload is missing");
        void Save(string path,BitmapSource image,CancellationToken token,string profile)
        {
            try {method!.Invoke(null,new object[]{path,image,token,profile});}
            catch(TargetInvocationException e) when(e.InnerException is not null) {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
        }
        foreach(var space in Enum.GetValues<ExportRgbSpace>().Where(x=>x is not (ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.VGamut))) foreach(string extension in new[]{".tif",".png"})
        {
            string profile=ExportColorSpace.ResolveProfile(space,repo);
            Check(File.Exists(profile),"Mapped profile missing");
            int channels=extension==".png"?4:3,width=257,height=2;
            var bytes=new byte[width*height*channels*2];
            for(int i=0;i<width*height;i++)
            {
                var rgb=ExportColorSpace.FromSrgb((i%257)/256d,.123456789,(i%37)/36d,space);
                double[] values=[rgb.Red,rgb.Green,rgb.Blue,(i%19)/18d];
                for(int c=0;c<channels;c++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((i*channels+c)*2,2),(ushort)Math.Round(Math.Clamp(values[c],0,1)*65535));
            }
            var bitmap=BitmapSource.Create(width,height,96,96,channels==3?PixelFormats.Rgb48:PixelFormats.Rgba64,null,bytes,width*channels*2);bitmap.Freeze();
            string path=Path.Combine(output,space+extension);Save(path,bitmap,CancellationToken.None,profile);
            using(var input=File.OpenRead(path))
            {
                var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);var frame=decoder.Frames[0];
                Check(frame.Format==bitmap.Format,"Export bit depth/alpha changed");
                Check(Copy(frame).SequenceEqual(bytes),"RGB16 pixels changed during encode/decode");
                Check(frame.ColorContexts?.Count==1,"Missing/ambiguous embedded ICC");
                using var embedded=frame.ColorContexts![0].OpenProfileStream();using var buffer=new MemoryStream();embedded.CopyTo(buffer);
                Check(buffer.ToArray().SequenceEqual(File.ReadAllBytes(profile)),"Embedded ICC differs from chosen profile");
            }
            Check(Copy(bitmap).SequenceEqual(bytes),"Save mutated input bitmap");
            Check(Enumerable.Range(0,width*height*channels).Any(i=>BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i*2,2))%257!=0),"Fixture collapsed to 8-bit");
            if(space==ExportRgbSpace.Srgb)
            {
                string legacy=Path.Combine(output,"legacy"+extension);SpektraExport.Save(legacy,bitmap,CancellationToken.None);
                Check(File.ReadAllBytes(legacy).SequenceEqual(File.ReadAllBytes(path)),"Legacy three-argument export changed encoded bytes");
            }
            string cancelled=Path.Combine(output,"cancelled"+extension);
            Reject<OperationCanceledException>(()=>Save(cancelled,bitmap,new CancellationToken(true),profile));
            Check(!File.Exists(cancelled),"Cancelled export wrote a file");
            if(space==ExportRgbSpace.Srgb && channels==3)
            {
                byte[] corrupt=File.ReadAllBytes(profile);"CMYK"u8.CopyTo(corrupt.AsSpan(16,4));
                string invalid=Path.Combine(output,"cmyk.icc");File.WriteAllBytes(invalid,corrupt);
                var sentinel=File.ReadAllBytes(path);
                Reject<InvalidDataException>(()=>Save(path,bitmap,CancellationToken.None,invalid));
                Check(File.ReadAllBytes(path).SequenceEqual(sentinel),"Rejected profile replaced existing output");
            }
        }
    }
    private static void ProfileAgreement()
    {
        using var fixtures=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));
        foreach(var space in new[]{ExportRgbSpace.AdobeRgb,ExportRgbSpace.ProPhotoRgb,ExportRgbSpace.LinearRec2020})
        {
            byte[] icc=File.ReadAllBytes(ExportColorSpace.ResolveProfile(space,repo));
            int U32(int at)=>(int)BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(at,4));
            int Tag(string name)
            {
                for(int i=0;i<U32(128);i++) if(System.Text.Encoding.ASCII.GetString(icc,132+i*12,4)==name) return U32(136+i*12);
                throw new Exception("Missing ICC tag: "+name);
            }
            double Fixed(int at)=>BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(at,4))/65536d;
            double Transfer(string name,double value)
            {
                int at=Tag(name);
                if(System.Text.Encoding.ASCII.GetString(icc,at,4)=="curv")
                {int count=U32(at+8);if(count==0)return value;if(count==1)return Math.Pow(value,BinaryPrimitives.ReadUInt16BigEndian(icc.AsSpan(at+12,2))/256d);throw new Exception("Unexpected table TRC in target fixture");}
                Check(System.Text.Encoding.ASCII.GetString(icc,at,4)=="para" && BinaryPrimitives.ReadUInt16BigEndian(icc.AsSpan(at+8,2))==3,"Unexpected target ICC parametric curve");
                return value<Fixed(at+28)?Fixed(at+24)*value:Math.Pow(Fixed(at+16)*value+Fixed(at+20),Fixed(at+12));
            }
            var tags=new[]{Tag("rXYZ"),Tag("gXYZ"),Tag("bXYZ")};double maximum=0;
            foreach(var fixture in fixtures.RootElement.GetProperty("profileCases").EnumerateArray().Where(x=>x.GetProperty("space").GetString()==space.ToString()))
            {
                var values=fixture.GetProperty("input");double[] linear=[Transfer("rTRC",values[0].GetDouble()),Transfer("gTRC",values[1].GetDouble()),Transfer("bTRC",values[2].GetDouble())];
                for(int row=0;row<3;row++)
                {double actual=Enumerable.Range(0,3).Sum(c=>Fixed(tags[c]+8+row*4)*linear[c]);double error=Math.Abs(actual-fixture.GetProperty("pcs")[row].GetDouble());maximum=Math.Max(maximum,error);Check(error<.0001,"ICC profile does not encode the nominal space");}
            }
            Console.WriteLine($"{space}: ICC matrix/TRC vs Colour PCS maximum {maximum:E3}");
        }
        var diagnostics=new System.Collections.Generic.Dictionary<string,int>();
        foreach(var space in new[]{ExportRgbSpace.AdobeRgb,ExportRgbSpace.ProPhotoRgb,ExportRgbSpace.LinearRec2020})
        {
            const int steps=17,count=steps*steps*steps;
            // WIC color transforms support RGBA64, but not WPF's RGB48 GUID.
            // Keep all channels at 16 bits, adding opaque alpha only.
            var bytes=new byte[count*8];var expected=new ushort[count*3];int pixel=0;
            for(int r=0;r<steps;r++) for(int g=0;g<steps;g++) for(int b=0;b<steps;b++)
            {
                var target=ExportColorSpace.FromSrgb(r/(steps-1d),g/(steps-1d),b/(steps-1d),space);
                double[] components=[target.Red,target.Green,target.Blue];var packed=new double[3];
                for(int c=0;c<3;c++) {ushort code=(ushort)Math.Round(Math.Clamp(components[c],0,1)*65535);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pixel*8+c*2,2),code);packed[c]=code/65535d;}
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pixel*8+6,2),65535);
                var back=ExportColorSpace.ToSrgb(packed[0],packed[1],packed[2],space);
                double[] reference=[back.Red,back.Green,back.Blue];
                for(int c=0;c<3;c++) expected[pixel*3+c]=(ushort)Math.Round(Math.Clamp(reference[c],0,1)*65535);
                pixel++;
            }
            var bitmap=BitmapSource.Create(count,1,96,96,PixelFormats.Rgba64,null,bytes,count*8);bitmap.Freeze();
            var wic=new ColorConvertedBitmap(bitmap,new ColorContext(new Uri(ExportColorSpace.ResolveProfile(space,repo))),
                new ColorContext(new Uri(ExportColorSpace.ResolveProfile(ExportRgbSpace.Srgb,repo))),PixelFormats.Rgba64);
            var actual=Copy(wic);int maximum=0, maximumIndex=0;
            for(int i=0;i<expected.Length;i++) {int difference=Math.Abs(BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan((i/3)*8+(i%3)*2,2))-expected[i]);if(difference>maximum){maximum=difference;maximumIndex=i;}}
            Console.WriteLine($"{space}: WIC-vs-double matrix/TRC max {maximum}/65535 codes, pixel/channel {maximumIndex/3}/{maximumIndex%3}, expected {expected[maximumIndex]} actual {BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan((maximumIndex/3)*8+(maximumIndex%3)*2,2))}");
            var icmInput=new WindowsIcm.Color[count];
            for(int i=0;i<count;i++) icmInput[i]=new WindowsIcm.Color{R=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i*8,2)),G=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i*8+2,2)),B=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i*8+4,2))};
            var icm=WindowsIcm.Convert(icmInput,ExportColorSpace.ResolveProfile(space,repo),ExportColorSpace.ResolveProfile(ExportRgbSpace.Srgb,repo));
            int icmMaximum=0;
            for(int i=0;i<count;i++) {icmMaximum=Math.Max(icmMaximum,Math.Abs(icm[i].R-expected[i*3]));icmMaximum=Math.Max(icmMaximum,Math.Abs(icm[i].G-expected[i*3+1]));icmMaximum=Math.Max(icmMaximum,Math.Abs(icm[i].B-expected[i*3+2]));}
            diagnostics[space.ToString()]=icmMaximum;
            Console.WriteLine($"{space}: explicit relative ICC16 maximum {icmMaximum}/65535");
            // A diagnostic, not a math oracle: both Windows APIs can differ
            // markedly near gamut boundaries. The independent profile PCS
            // validation above tests the actual matrix/TRC data instead.
            Check(icmMaximum==maximum,"WIC and explicit Windows ICC diagnostic changed differently");
        }
        File.WriteAllText(Path.Combine(output,"wic-profile-agreement.json"),JsonSerializer.Serialize(diagnostics));
    }
}
