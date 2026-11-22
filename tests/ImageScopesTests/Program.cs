using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int assertions, failures;
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    [STAThread]
    private static int Main()
    {
        Test("Known color histogram, luma, waveform and clipping", KnownColors);
        Test("Neutral ramp stays centered in vectorscope and maps waveform positions", Ramp);
        Test("Full-range Rec.709 chroma primary and secondary targets", Chroma);
        Test("Transparency does not create black clipping; partial alpha uses straight RGB", Alpha);
        Test("Deterministic bounded sampling retains source coordinates and long/thin frames", Sampling);
        Test("Independent random oracle agrees in every bin without changing input", RandomOracle);
        Test("Invalid dimensions/buffer and cancellation are rejected", Validation);
        Test("Bitmap extraction, BGRA/RGB/PBGRA formats and unchanged source", BitmapFormats);
        Test("Latest frame wins, cached identity and close cancel; offscreen plots render", WindowUpdates);
        Test("Large-frame bounded analysis diagnostic", LargeFrame);
        Console.WriteLine($"{assertions} assertions; {failures} failing groups.");
        return failures == 0 ? 0 : 1;
    }
    private static void KnownColors()
    {
        byte[] pixels = [0,0,0,255, 255,255,255,255, 0,0,255,255, 0,255,0,255, 255,0,0,255];
        var data = ImageScopesMath.Analyze(pixels, ImageScopesMath.Plan(5,1));
        Check(data.VisiblePixels == 5, "Five opaque pixels must be counted");
        Check(data.BlackPixels == 1 && data.WhitePixels == 1 && data.AnyChannelLow == 4 && data.AnyChannelHigh == 4, "Clipping counts incorrect");
        Check(data.Low.SequenceEqual(new long[]{3,3,3}) && data.High.SequenceEqual(new long[]{2,2,2}), "Channel clipping incorrect");
        foreach(var channel in data.Histogram) Check(channel.Sum() == 5, "Histogram population differs");
        foreach(var channel in data.Waveform) Check(channel.Sum() == 5, "Waveform population differs");
        Check(data.Vectorscope.Sum() == 5, "Vectorscope population differs");
        foreach(int bin in new[]{0,255,54,182,18}) Check(data.Histogram[3][bin] == 1, "Rec.709 code luma bin incorrect: " + bin);
    }
    private static void Ramp()
    {
        var pixels = new byte[256 * 4];
        for(int i=0;i<256;i++) { pixels[4*i]=pixels[4*i+1]=pixels[4*i+2]=(byte)i; pixels[4*i+3]=255; }
        var data=ImageScopesMath.Analyze(pixels, ImageScopesMath.Plan(256,1));
        for(int i=0;i<256;i++) for(int c=0;c<4;c++)
        { Check(data.Histogram[c][i]==1,"Ramp histogram differs"); Check(data.Waveform[c][i*256+i]==1,"Ramp waveform differs"); }
        Check(data.Vectorscope[128*256+128]==256,"Neutral ramp must have zero chroma");
    }
    private static void Chroma()
    {
        var targets = new[]{(255,0,0,98,255),(0,255,0,29,12),(0,0,255,255,116),
            (0,255,255,157,0),(255,0,255,226,243),(255,255,0,0,139)};
        foreach(var (r,g,b,x,y) in targets)
        {
            var actual=ImageScopesMath.VectorBin(r,g,b);
            Check(actual == (x,y), $"Chroma target differs: {r},{g},{b} => {actual}");
        }
    }
    private static void Alpha()
    {
        byte[] pixels=[0,0,0,0, 0,0,255,128, 127,127,127,255];
        var data=ImageScopesMath.Analyze(pixels,ImageScopesMath.Plan(3,1));
        Check(data.VisiblePixels==2 && data.TransparentPixels==1 && data.BlackPixels==0,"Transparent black was counted");
        Check(data.Histogram[0][255]==1 && data.AnyChannelHigh==1,"Partial alpha changed straight RGB");
    }
    private static void Sampling()
    {
        foreach(var (w,h) in new[]{(1,1),(37,29),(8514,5676),(16384,1),(1,16384),(1000,10000),(10000,1000)})
        foreach(int cap in new[]{1,7,1000,1048576})
        {
            var plan=ImageScopesMath.Plan(w,h,cap);
            Check(plan.Count <= cap && plan.Width<=w && plan.Height<=h && plan.Count>0,"Sampling bound violated");
            Check(plan==ImageScopesMath.Plan(w,h,cap),"Sampling is not deterministic");
            if((long)w*h<=cap) Check(plan.IsFull,"Small image was unnecessarily sampled");
            var xs=Enumerable.Range(0,plan.Width).Select(plan.SourceX).ToArray();
            var ys=Enumerable.Range(0,plan.Height).Select(plan.SourceY).ToArray();
            Check(xs[0]>=0 && xs[^1]<w && xs.Distinct().Count()==xs.Length,"Sample x indices invalid");
            Check(ys[0]>=0 && ys[^1]<h && ys.Distinct().Count()==ys.Length,"Sample y indices invalid");
        }
    }
    private static void RandomOracle()
    {
        var plan=ImageScopesMath.Plan(997,331,6000); var pixels=new byte[plan.Count*4]; new Random(2701).NextBytes(pixels);
        var original=(byte[])pixels.Clone(); var actual=ImageScopesMath.Analyze(pixels,plan);
        int[][] hist=Enumerable.Range(0,4).Select(_=>new int[256]).ToArray();
        int[][] wave=Enumerable.Range(0,4).Select(_=>new int[65536]).ToArray(); var vector=new int[65536];
        int visible=0;
        for(int y=0;y<plan.Height;y++) for(int x=0;x<plan.Width;x++)
        {
            int p=(y*plan.Width+x)*4; if(pixels[p+3]==0) continue; visible++;
            int r=pixels[p+2],g=pixels[p+1],b=pixels[p];
            double luma=(2126*r+7152*g+722*b)/10000d;
            int[] bins=[r,g,b,(int)Math.Floor(luma+.5)];
            int column=plan.SourceWidth==1?128:(int)Math.Floor(plan.SourceX(x)*255d/(plan.SourceWidth-1)+.5);
            for(int c=0;c<4;c++) { hist[c][bins[c]]++; wave[c][bins[c]*256+column]++; }
            int cb=(int)Math.Floor(127.5+(b-luma)*.5/.9278+.5);
            int cr=(int)Math.Floor(127.5+(r-luma)*.5/.7874+.5);
            vector[Math.Clamp(cr,0,255)*256+Math.Clamp(cb,0,255)]++;
        }
        Check(actual.VisiblePixels==visible,"Opaque population differs");
        for(int c=0;c<4;c++) { Check(hist[c].SequenceEqual(actual.Histogram[c]),"Random histogram differs"); Check(wave[c].SequenceEqual(actual.Waveform[c]),"Random waveform differs"); }
        Check(vector.SequenceEqual(actual.Vectorscope),"Random vectorscope differs");
        Check(pixels.SequenceEqual(original),"Scope analysis modified source bytes");
    }
    private static void Validation()
    {
        Reject<ArgumentOutOfRangeException>(()=>ImageScopesMath.Plan(0,1));
        Reject<ArgumentOutOfRangeException>(()=>ImageScopesMath.Plan(1,0));
        Reject<ArgumentOutOfRangeException>(()=>ImageScopesMath.Plan(1,1,0));
        Reject<ArgumentException>(()=>ImageScopesMath.Analyze(new byte[3],ImageScopesMath.Plan(1,1)));
        var cancelled=new CancellationToken(true);
        Reject<OperationCanceledException>(()=>ImageScopesMath.Analyze(new byte[4],ImageScopesMath.Plan(1,1),cancelled));
    }
    private static BitmapSource Bitmap(byte[] bytes, PixelFormat format, int width, int height)
    {
        var frame=BitmapSource.Create(width,height,96,96,format,null,bytes,(width*format.BitsPerPixel+7)/8); frame.Freeze(); return frame;
    }
    private static byte[] Bytes(BitmapSource source)
    {
        int stride=(source.PixelWidth*source.Format.BitsPerPixel+7)/8;
        var bytes=new byte[stride*source.PixelHeight]; source.CopyPixels(bytes,stride,0); return bytes;
    }
    private static void BitmapFormats()
    {
        byte[] source=[0,0,255,255, 0,255,0,255, 255,0,0,255, 18,25,47,0, 128,128,128,255, 255,255,255,255];
        var frame=Bitmap(source,PixelFormats.Bgra32,3,2);
        var before=Bytes(frame);
        Check(ReferenceEquals(frame,ImageScopes.FreezeSnapshot(frame)),"Frozen input was needlessly cloned");
        var actual=Task.Run(()=>ImageScopes.Compute(frame,CancellationToken.None)).GetAwaiter().GetResult();
        var expected=ImageScopesMath.Analyze(source,ImageScopesMath.Plan(3,2));
        for(int c=0;c<4;c++) Check(actual.Data.Histogram[c].SequenceEqual(expected.Histogram[c]),"BGRA extraction changed codes");
        Check(actual.RgbWaveform.IsFrozen && actual.Vector.IsFrozen && actual.LumaWaveform.IsFrozen,"Scope images not frozen");
        Check(Bytes(frame).SequenceEqual(before),"Bitmap pixels changed");
        var rgb=Bitmap(new byte[]{255,0,0,0,255,0,0,0,255},PixelFormats.Rgb24,3,1);
        var rgbResult=Task.Run(()=>ImageScopes.Compute(rgb,CancellationToken.None)).GetAwaiter().GetResult();
        Check(rgbResult.Data.VisiblePixels==3 && rgbResult.Data.Histogram[3][54]==1 && rgbResult.Data.Histogram[3][182]==1 && rgbResult.Data.Histogram[3][18]==1,"RGB24 conversion incorrect");
        var premultiplied=Bitmap(new byte[]{0,0,128,128,0,0,0,0},PixelFormats.Pbgra32,2,1);
        var pbgra=Task.Run(()=>ImageScopes.Compute(premultiplied,CancellationToken.None)).GetAwaiter().GetResult();
        Check(pbgra.Data.Histogram[0][255]==1 && pbgra.Data.TransparentPixels==1,"Premultiplied alpha was not straightened");
        var mutable=new WriteableBitmap(frame); var snapshot=ImageScopes.FreezeSnapshot(mutable);
        Check(snapshot.IsFrozen && !ReferenceEquals(snapshot,mutable) && !mutable.IsFrozen,"Snapshot mutated caller freeze state");
        mutable.WritePixels(new Int32Rect(0,0,1,1),new byte[]{0,0,0,255},4,0);
        Check(Bytes(snapshot).SequenceEqual(before),"Immutable snapshot followed later writes");
        var gridBytes=new byte[37*29*4];
        for(int i=0;i<37*29;i++){gridBytes[i*4]=(byte)(i%256);gridBytes[i*4+1]=(byte)((i/37)*7);gridBytes[i*4+2]=(byte)((i%37)*6);gridBytes[i*4+3]=255;}
        var sampled=ImageScopes.Compute(Bitmap(gridBytes,PixelFormats.Bgra32,37,29),CancellationToken.None,31);
        var plan=sampled.Data.Sampling; var extracted=new byte[plan.Count*4];
        for(int y=0;y<plan.Height;y++) for(int x=0;x<plan.Width;x++)
            Array.Copy(gridBytes,(plan.SourceY(y)*37+plan.SourceX(x))*4,extracted,(y*plan.Width+x)*4,4);
        var reference=ImageScopesMath.Analyze(extracted,plan);
        for(int c=0;c<4;c++) Check(sampled.Data.Histogram[c].SequenceEqual(reference.Histogram[c]),"Bounded bitmap samples shifted");
        Check(sampled.Data.Vectorscope.SequenceEqual(reference.Vectorscope),"Sampled vector extraction differs");
        Reject<OperationCanceledException>(()=>ImageScopes.Compute(frame,new CancellationToken(true)));
        var empty=ImageScopes.Compute(Bitmap(new byte[4],PixelFormats.Bgra32,1,1),CancellationToken.None);
        Check(!ImageScopes.DescribeClipping(empty.Data).Contains("NaN") && !ImageScopes.DescribeClipping(empty.Data).Contains("Infinity"),"Empty visible population divided by zero");
    }
    private static void Pump(Task task)
    {
        var frame=new DispatcherFrame(); var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        var time=Stopwatch.StartNew();
        timer.Tick+=(_,_)=>{if(task.IsCompleted || time.Elapsed.TotalSeconds>15) frame.Continue=false;};
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Check(task.IsCompleted,"UI update timed out"); task.GetAwaiter().GetResult();
    }
    private static void WindowUpdates()
    {
        var window=new ImageScopes.ScopeWindow("Scope test, never shown");
        byte[] many=new byte[2048*2048*4]; Array.Fill(many,(byte)255);
        var first=Bitmap(many,PixelFormats.Bgra32,2048,2048);
        window.Update(first); var previous=window.PendingUpdate;
        var last=Bitmap(new byte[]{0,0,255,255},PixelFormats.Bgra32,1,1);
        window.Update(last); var latest=window.PendingUpdate;
        Pump(Task.WhenAll(previous,latest));
        Check(window.CurrentResult?.VisiblePixels==1 && window.CurrentResult.Histogram[0][255]==1 && window.CurrentResult.Histogram[1][0]==1,"A stale frame replaced the latest result");
        window.Update(last); Check(ReferenceEquals(latest,window.PendingUpdate),"Same frozen bitmap recomputed");
        var bars=new byte[512*256*4];
        for(int y=0;y<256;y++) for(int x=0;x<512;x++)
        {int p=(y*512+x)*4;bars[p]=(byte)(x/2);bars[p+1]=(byte)y;bars[p+2]=(byte)(255-x/2);bars[p+3]=255;}
        window.Update(Bitmap(bars,PixelFormats.Bgra32,512,256)); Pump(window.PendingUpdate);
        var content=(FrameworkElement)window.Content; content.Measure(new Size(1020,700)); content.Arrange(new Rect(0,0,1020,700)); content.UpdateLayout();
        var image=new RenderTargetBitmap(1020,700,96,96,PixelFormats.Pbgra32); image.Render(content);
        string directory=Path.Combine("artifacts","full-function-20261116"); Directory.CreateDirectory(directory);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using(var output=File.Create(Path.Combine(directory,"image-scopes-offscreen.png"))) encoder.Save(output);
        Check(Bytes(image).Any(x=>x!=0),"Offscreen plots were blank");
        window.Update(first); var closing=window.PendingUpdate; window.Close(); Pump(closing);
        Check(window.IsClosed && window.CurrentResult is null,"Closed window retained/published frame data");
        window.Update(last); Check(ReferenceEquals(closing,window.PendingUpdate),"Closed window started work");
    }
    private static void LargeFrame()
    {
        const int width=8514, height=5676;
        var pixels=new byte[width*height*4];
        for(int i=0;i<pixels.Length;i+=4) {pixels[i]=(byte)(i>>2);pixels[i+1]=128;pixels[i+2]=64;pixels[i+3]=255;}
        var frame=Bitmap(pixels,PixelFormats.Bgra32,width,height);
        var watch=Stopwatch.StartNew();
        var result=Task.Run(()=>ImageScopes.Compute(frame,CancellationToken.None)).GetAwaiter().GetResult();
        watch.Stop();
        Check(result.Data.VisiblePixels<=ImageScopesMath.DefaultMaximumSamples && result.Data.Sampling.SourceWidth==width,"Large frame exceeded bounded sample budget");
        Check(result.Data.Histogram[0][64]==result.Data.VisiblePixels && result.Data.Histogram[1][128]==result.Data.VisiblePixels,"Large-frame constant channels differ");
        var info=$"8514x5676 -> {result.Data.Sampling.Width}x{result.Data.Sampling.Height} samples; {watch.Elapsed.TotalMilliseconds:F1} ms analysis+density, background worker; timing diagnostic only";
        Console.WriteLine(info);
        File.WriteAllText(Path.Combine("artifacts","full-function-20261116","image-scopes-performance.txt"),info);
    }
}
