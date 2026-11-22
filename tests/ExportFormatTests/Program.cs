using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int checks, failures;
    private static string output="", repo="";
    private static readonly List<object> dngs=new(), jpegs=new();
    private static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    private static void Test(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e);}}
    private static void Reject<T>(Action action) where T:Exception {try{action();}catch(T){checks++;return;}throw new Exception("Expected "+typeof(T).Name);}
    [STAThread] private static int Main()
    {
        repo=Path.GetFullPath(".");output=Path.Combine(repo,"artifacts/full-function-20261116/export-formats",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        Test("DNG writes all headroom boundaries and reports exact RGB clipping counts", Dng);
        Test("JPEG explicitly quantizes RGB16, embeds four ICC spaces, and honors quality", Jpeg);
        Test("Invalid format, finite values, quality and cancellation preserve the destination", Invalid);
        Test("Cancellation during streamed DNG rows preserves the previous file", MidWriteCancel);
        File.WriteAllText(Path.Combine(output,"manifest.json"),JsonSerializer.Serialize(new{dngs,jpegs,checks,failures},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(repo,"artifacts/full-function-20261116/export-formats-latest.txt"),output);
        Console.WriteLine($"{checks} assertions; {failures} failing groups; artifacts {output}");return failures==0?0:1;
    }
    private static void Dng()
    {
        foreach(float peak in new[]{.8f,1f,1.0001f,2f,2.0001f,4f,8f,16f,16.25f,float.MaxValue})
        {
            const int w=65,h=49;var rgba=new float[w*h*4];var rng=new Random(173);
            for(int p=0;p<w*h;p++){for(int c=0;c<3;c++)rgba[p*4+c]=(float)(rng.NextDouble()*.8-.01);rgba[p*4+3]=float.NaN;}
            rgba[0]=peak;rgba[1]=-.75f;rgba[2]=.18f;
            var before=MemoryMarshal.AsBytes(rgba.AsSpan()).ToArray();string stem="linear-"+dngs.Count,path=Path.Combine(output,stem+".dng");
            var info=SpektraExport.SaveLinearDng(path,rgba,w,h,CancellationToken.None);
            int stops=peak<=1?0:Math.Min(4,(int)Math.Ceiling(Math.Log2(peak)));
            Check(info.HeadroomStops==stops,"Wrong headroom at "+peak);Check(info.Peak==peak,"Alpha affected peak");
            Check(info.NegativeSamples==rgba.Where((_,i)=>i%4<3).Count(v=>v<0),"Negative samples count mismatch");
            Check(info.OverRangeSamples==rgba.Where((_,i)=>i%4<3).Count(v=>v>16),"Upper clipping count mismatch");
            Check(MemoryMarshal.AsBytes(rgba.AsSpan()).SequenceEqual(before),"DNG writer modified input");
            Check(new FileInfo(path).Length>w*h*6,"DNG lacks metadata or pixels");
            File.WriteAllBytes(Path.Combine(output,stem+".f32"),before);
            dngs.Add(new{file=stem+".dng",source=stem+".f32",width=w,height=h,info});
        }
        var negative=new float[]{-1,-2,-3,1};var all=SpektraExport.SaveLinearDng(Path.Combine(output,"all-negative.dng"),negative,1,1,default);
        Check(all.Peak==-1 && all.HeadroomStops==0 && all.NegativeSamples==3,"All-negative frame policy");
    }
    private static BitmapSource Rgb(ushort[] pixels,int w,int h)
    {var bmp=BitmapSource.Create(w,h,96,96,PixelFormats.Rgb48,null,pixels,w*6);bmp.Freeze();return bmp;}
    private static void Jpeg()
    {
        const int w=256,h=128;var pixels=new ushort[w*h*3];var rng=new Random(9618);
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)for(int c=0;c<3;c++)
            pixels[(y*w+x)*3+c]=y<64?(ushort)(new[]{257,20003,58001}[c]):(ushort)rng.Next(65536);
        var image=Rgb(pixels,w,h);File.WriteAllBytes(Path.Combine(output,"jpeg-source.u16"),MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
        foreach(var space in Enum.GetValues<ExportRgbSpace>().Where(x=>x is not (ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.VGamut)))foreach(int quality in new[]{60,92,100})
        {
            string profile=ExportColorSpace.ResolveProfile(space,repo),name=$"{space}-{quality}.jpg",path=Path.Combine(output,name);
            SpektraExport.SaveJpegWithProfile(path,image,default,profile,quality);
            using var f=File.OpenRead(path);var decoded=BitmapDecoder.Create(f,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.PixelWidth==w && decoded.PixelHeight==h,"JPEG dimensions changed");
            Check(decoded.Format.BitsPerPixel==24 || decoded.Format.BitsPerPixel==32,"JPEG is not 8 bits/channel");
            Check(decoded.ColorContexts?.Count==1,"JPEG has no ICC context");
            using var icc=decoded.ColorContexts![0].OpenProfileStream();using var copy=new MemoryStream();icc.CopyTo(copy);
            Check(copy.ToArray().SequenceEqual(File.ReadAllBytes(profile)),"JPEG ICC bytes changed");
            jpegs.Add(new{file=name,source="jpeg-source.u16",profile,quality,width=w,height=h});
        }
        var after=new ushort[pixels.Length];image.CopyPixels(after,w*6,0);Check(after.SequenceEqual(pixels),"JPEG changed source RGB16");
        Check(new FileInfo(Path.Combine(output,"Srgb-100.jpg")).Length>new FileInfo(Path.Combine(output,"Srgb-60.jpg")).Length,"Quality setting had no effect");
    }
    private static void Invalid()
    {
        string path=Path.Combine(output,"protected.dng"),jpg=Path.Combine(output,"protected.jpg");var sentinel=new byte[]{4,2,9};File.WriteAllBytes(path,sentinel);File.WriteAllBytes(jpg,sentinel);
        var valid=new float[]{.1f,.2f,.3f,1};var cancelled=new CancellationToken(true);
        Reject<OperationCanceledException>(()=>SpektraExport.SaveLinearDng(path,valid,1,1,cancelled));
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})Reject<InvalidDataException>(()=>SpektraExport.SaveLinearDng(path,new float[]{bad,0,0,1},1,1,default));
        Reject<ArgumentException>(()=>SpektraExport.SaveLinearDng(path,valid,2,1,default));
        Reject<ArgumentException>(()=>SpektraExport.SaveLinearDng(path,Array.Empty<float>(),0,0,default));
        Reject<InvalidDataException>(()=>SpektraExport.SaveLinearDng(jpg,valid,1,1,default));
        var rgb=Rgb(new ushort[]{1,2,3},1,1);var profile=ExportColorSpace.ResolveProfile(ExportRgbSpace.Srgb,repo);
        Reject<OperationCanceledException>(()=>SpektraExport.SaveJpegWithProfile(jpg,rgb,cancelled,profile,92));
        foreach(int q in new[]{-1,0,101})Reject<ArgumentOutOfRangeException>(()=>SpektraExport.SaveJpegWithProfile(jpg,rgb,default,profile,q));
        Reject<InvalidDataException>(()=>SpektraExport.SaveJpegWithProfile(path,rgb,default,profile,92));
        var rgba=BitmapSource.Create(1,1,96,96,PixelFormats.Rgba64,null,new ushort[]{1,2,3,4},8);
        Reject<InvalidDataException>(()=>SpektraExport.SaveJpegWithProfile(jpg,rgba,default,profile,92));
        Check(File.ReadAllBytes(path).SequenceEqual(sentinel) && File.ReadAllBytes(jpg).SequenceEqual(sentinel),"Failed export overwrote destination");
        Check(!Directory.EnumerateFiles(output,".spektra-*.tmp").Any(),"Export left temporary files");
    }
    private static void MidWriteCancel()
    {
        var path=Path.Combine(output,"midwrite.dng");var sentinel=new byte[]{9,1,4};File.WriteAllBytes(path,sentinel);
        using var cancel=new CancellationTokenSource();var rgba=new float[65*49*4];
        try
        {
            PanelFiles.WriteAtomically(path,stream=>
            {
                using var wrapper=new CancelStream(stream,cancel);
                typeof(LinearDngWriter).GetMethod("Write",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,
                    new object[]{wrapper,new ReadOnlyMemory<float>(rgba),65,49,new LinearDngInfo(0,0,0,0),cancel.Token});
            });
            throw new Exception("Streamed write did not observe cancellation");
        }
        catch(TargetInvocationException e) when(e.InnerException is OperationCanceledException){checks++;}
        Check(cancel.IsCancellationRequested,"Test did not cancel during actual output");
        Check(File.ReadAllBytes(path).SequenceEqual(sentinel),"Cancelled write published partial DNG");
        Check(!Directory.EnumerateFiles(output,".spektra-*.tmp").Any(),"Cancelled DNG leaked temporary file");
    }
    private sealed class CancelStream(Stream target,CancellationTokenSource cancel):Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>target.CanSeek;public override bool CanWrite=>true;
        public override long Length=>target.Length;public override long Position{get=>target.Position;set=>target.Position=value;}
        public override void Flush()=>target.Flush();public override long Seek(long p,SeekOrigin o)=>target.Seek(p,o);public override void SetLength(long n)=>target.SetLength(n);
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();
        public override void Write(byte[] b,int o,int n){target.Write(b,o,n);if(target.Position>1500)cancel.Cancel();}
        public override void Write(ReadOnlySpan<byte> b){target.Write(b);if(target.Position>1500)cancel.Cancel();}
        // The enclosing atomic writer owns and closes the underlying file.
    }
}
