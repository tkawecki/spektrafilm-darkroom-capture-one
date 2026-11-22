// SPDX-License-Identifier: GPL-3.0-only
// Strip delta/label/span semantics from The Dyeworks 2.2.3 test_strip.py.
// Upstream: Andrea Volpato / Celian Le Bolloch. Full frames are composed only
// after rendering, so spatial effects and image-dependent measurements retain
// the same source context. This helper never changes photo settings.
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed record ExposureBand(int Index,double Value,double Delta,string Label);
internal sealed record ExposureComparisonFrame(ExposureBand Band,BitmapSource Image);

internal static class ExposureComparisonModel
{
    internal static ExposureBand[] Plan(double baseValue,int count,double step,double minimum,double maximum)
    {
        if(!double.IsFinite(baseValue)||!double.IsFinite(step)||!double.IsFinite(minimum)||!double.IsFinite(maximum)
            ||minimum>=maximum||count is not (3 or 5)||!(step==1||step==.5||Math.Abs(step-1d/3)<1e-12))
            throw new ArgumentException("Choose 3 or 5 exposures and a 1/3, 1/2 or 1 EV step.");
        var bands=Enumerable.Range(0,count).Select(i=>{var delta=(i-(count-1)/2d)*step;return new ExposureBand(i,baseValue+delta,delta,Label(delta));}).ToArray();
        if(bands.Any(x=>x.Value<minimum||x.Value>maximum))
            throw new ArgumentException($"This bracket exceeds the supported exposure range {minimum:G} to {maximum:G} EV. Choose fewer frames or a smaller step; values are not clipped.");
        return bands;
    }
    internal static string Label(double delta)
    {
        if(Math.Abs(delta)<1e-9)return "0";
        var sign=delta>0?"+":"-";var magnitude=Math.Abs(delta);int whole=(int)(magnitude+1e-9);var fraction=magnitude-whole;
        foreach(var (numerator,denominator) in new[]{(1,3),(2,3),(1,2)})if(Math.Abs(fraction-(double)numerator/denominator)<1e-6)
            return sign+(whole>0?whole.ToString(CultureInfo.InvariantCulture)+" ":"")+numerator+"/"+denominator;
        return sign+(fraction<1e-6?whole.ToString(CultureInfo.InvariantCulture):magnitude.ToString("G",CultureInfo.InvariantCulture));
    }
    internal static (int Start,int End)[] Spans(int width,int count)
    {
        if(count<1||width<count)throw new ArgumentException("The image must have at least one pixel per exposure band.");
        return Enumerable.Range(0,count).Select(i=>((int)Math.Round((double)i*width/count),(int)Math.Round((double)(i+1)*width/count))).ToArray();
    }
    internal static BitmapSource Compose(BitmapSource[] images)
    {
        if(images.Length==0)throw new ArgumentException("At least one full frame is required.");
        var first=images[0];int width=first.PixelWidth,height=first.PixelHeight,stride=checked(width*4);
        var spans=Spans(width,images.Length);var output=new byte[checked(height*stride)];
        for(int i=0;i<images.Length;i++)
        {
            var image=images[i];if(image.PixelWidth!=width||image.PixelHeight!=height)throw new ArgumentException("Comparison frames must have the same dimensions.");
            if(!image.IsFrozen)throw new ArgumentException("Comparison frames must be frozen snapshots.");
            if(image.Format!=PixelFormats.Bgra32){image=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);image.Freeze();}
            var (start,end)=spans[i];int bandStride=checked((end-start)*4);var cropped=new byte[checked(bandStride*height)];
            image.CopyPixels(new Int32Rect(start,0,end-start,height),cropped,bandStride,0);
            for(int y=0;y<height;y++)Buffer.BlockCopy(cropped,y*bandStride,output,y*stride+start*4,bandStride);
        }
        var result=BitmapSource.Create(width,height,first.DpiX,first.DpiY,PixelFormats.Bgra32,null,output,stride);result.Freeze();return result;
    }
}

// Generation and cancellation rules are separate from WPF events so late
// completions and stale-photo Apply actions can be tested without opening UI.
internal sealed class ExposureComparisonSession(double baseValue,double minimum,double maximum):IDisposable
{
    private readonly object sync=new();
    private readonly SemaphoreSlim rendererGate=new(1,1);
    private CancellationTokenSource? pending;
    private long generation;
    private bool disposed;
    private ExposureComparisonFrame[] results=Array.Empty<ExposureComparisonFrame>();
    internal ExposureComparisonFrame[] Results {get{lock(sync)return (ExposureComparisonFrame[])results.Clone();}}
    internal bool Ready {get{lock(sync)return !disposed&&pending is null&&results.Length>0;}}
    internal async Task RenderAsync(int count,double step,Func<double,CancellationToken,Task<BitmapSource>> render,Func<bool> isCurrent,
        CancellationToken stop,Action<int,int>? progress=null)
    {
        Invalidate();var plan=ExposureComparisonModel.Plan(baseValue,count,step,minimum,maximum);
        using var work=CancellationTokenSource.CreateLinkedTokenSource(stop);long stamp;
        lock(sync){ObjectDisposedException.ThrowIf(disposed,this);pending=work;stamp=++generation;}
        try
        {
            var frames=new ExposureComparisonFrame[plan.Length];
            for(int i=0;i<plan.Length;i++)
            {
                work.Token.ThrowIfCancellationRequested();if(!isCurrent())throw new InvalidOperationException("The photo or settings changed. Reopen the comparison to capture the current look.");
                await rendererGate.WaitAsync(work.Token);
                BitmapSource image;
                try{image=await render(plan[i].Value,work.Token);}
                finally{rendererGate.Release();}
                work.Token.ThrowIfCancellationRequested();if(!isCurrent())throw new InvalidOperationException("The photo or settings changed during rendering. Reopen the comparison.");
                if(image is null)throw new InvalidOperationException("The comparison renderer returned no image.");
                if(!image.IsFrozen){image=image.CloneCurrentValue();image.Freeze();}
                if(i>0&&(image.PixelWidth!=frames[0].Image.PixelWidth||image.PixelHeight!=frames[0].Image.PixelHeight))throw new InvalidOperationException("The comparison renderer changed source dimensions between exposures.");
                frames[i]=new(plan[i],image);progress?.Invoke(i+1,plan.Length);
            }
            lock(sync){work.Token.ThrowIfCancellationRequested();if(stamp!=generation||disposed)throw new OperationCanceledException();results=frames;}
        }
        finally
        {
            lock(sync){if(ReferenceEquals(pending,work))pending=null;}
        }
    }
    internal bool TryApply(int index,Func<double,bool> apply,Func<bool> isCurrent)
    {
        if(!isCurrent()){Invalidate();return false;}
        double value;lock(sync){if(!Ready||index<0||index>=results.Length)return false;value=results[index].Band.Value;}
        return apply(value);
    }
    internal void Invalidate()
    {
        CancellationTokenSource? old;lock(sync){generation++;results=Array.Empty<ExposureComparisonFrame>();old=pending;pending=null;}
        try{old?.Cancel();}catch(ObjectDisposedException){ }
    }
    public void Dispose(){lock(sync)disposed=true;Invalidate();}
}
