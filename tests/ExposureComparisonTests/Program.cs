using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int checks;
    private static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    private static void Reject(Action action,string message){checks++;try{action();}catch(ArgumentException){return;}throw new Exception(message);}
    private static BitmapSource Frame(byte value,int width=11,int height=3)
    {var data=Enumerable.Repeat(value,width*height*4).ToArray();var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);image.Freeze();return image;}
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var repo=Path.GetFullPath(args.Length>0?args[0]:".");
            var oracle=JsonNode.Parse(File.ReadAllText(Path.Combine(repo,"tests/ExposureComparisonTests/reference-strip-oracle.json")))!;
            // Optional second argument: the reference test_strip.py the oracle was made from, to confirm its hash.
            var sourcePath=args.Length>1?args[1]:null;
            if(sourcePath is not null)
                Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).Equals(oracle["source_sha256"]!.GetValue<string>(),StringComparison.OrdinalIgnoreCase),"Oracle matches pinned upstream source");
            foreach(var plan in oracle["plans"]!.AsArray())
            {
                var actual=ExposureComparisonModel.Plan(0,plan!["count"]!.GetValue<int>(),plan["step"]!.GetValue<double>(),-8,8);
                Check(actual.Select(x=>x.Delta).SequenceEqual(plan["deltas"]!.AsArray().Select(x=>x!.GetValue<double>()))&&actual.Select(x=>x.Label).SequenceEqual(plan["labels"]!.AsArray().Select(x=>x!.GetValue<string>())),"Actual reference delta/label oracle");
            }
            foreach(var spans in oracle["spans"]!.AsArray())
            {
                var actual=ExposureComparisonModel.Spans(spans!["width"]!.GetValue<int>(),spans["count"]!.GetValue<int>());
                Check(actual.SequenceEqual(spans["spans"]!.AsArray().Select(x=>(x![0]!.GetValue<int>(),x[1]!.GetValue<int>()))),"Actual reference band partition oracle");
            }
            foreach(var image in oracle["composites"]!.AsArray())
            {
                var actual=ExposureComparisonModel.Compose(Enumerable.Range(0,image!["count"]!.GetValue<int>()).Select(i=>Frame((byte)(10*(i+1)))).ToArray());
                var data=new byte[11*3*4];actual.CopyPixels(data,44,0);
                Check(Convert.ToHexString(SHA256.HashData(data))==image["sha256"]!.GetValue<string>(),"Actual reference composition byte hash");
            }
            var bands=ExposureComparisonModel.Plan(.25,5,.5,-8,8);
            Check(bands.Select(x=>x.Value).SequenceEqual(new[]{-.75,-.25,.25,.75,1.25}),"Centered absolute exposures");
            Check(bands.Select(x=>x.Label).SequenceEqual(new[]{"-1","-1/2","0","+1/2","+1"}),"reference fraction labels");
            Check(ExposureComparisonModel.Plan(0,5,1d/3,-8,8).Select(x=>x.Label).SequenceEqual(new[]{"-2/3","-1/3","0","+1/3","+2/3"}),"reference third-stop labels");
            Reject(()=>ExposureComparisonModel.Plan(7,5,1,-8,8),"Range must not silently clamp");
            Reject(()=>ExposureComparisonModel.Plan(double.NaN,3,1,-8,8),"Nonfinite base must fail");
            Reject(()=>ExposureComparisonModel.Plan(0,4,1,-8,8),"Only supported UI counts");
            foreach(var width in new[]{5,6,7,8,11,1600})foreach(var count in new[]{3,5})
            {
                var spans=ExposureComparisonModel.Spans(width,count);
                Check(spans[0].Start==0&&spans[^1].End==width&&spans.Zip(spans.Skip(1)).All(p=>p.First.End==p.Second.Start),"Contiguous spans");
                Check(spans.All(x=>x.End>x.Start),"Nonempty spans");
            }
            var composite=ExposureComparisonModel.Compose(new[]{Frame(10),Frame(20),Frame(30)});
            var pixels=new byte[11*3*4];composite.CopyPixels(pixels,44,0);
            var ranges=ExposureComparisonModel.Spans(11,3);
            for(int band=0;band<3;band++)for(int y=0;y<3;y++)for(int x=ranges[band].Start;x<ranges[band].End;x++)
                Check(pixels[(y*11+x)*4]==(band+1)*10,"Composite is exact full-frame band crop");
            using var session=new ExposureComparisonSession(0,-8,8);
            int applyCount=0,renderCount=0;double applied=0;
            await session.RenderAsync(3,.5,(ev,token)=>{renderCount++;return Task.FromResult(Frame((byte)(100+ev*20)));},()=>true,CancellationToken.None);
            Check(renderCount==3&&applyCount==0&&session.Ready,"Rendering does not apply settings");
            Check(session.TryApply(2,ev=>{applyCount++;applied=ev;return true;},()=>true)&&applied==.5&&applyCount==1,"Explicit Apply adopts only chosen absolute EV");
            Check(!session.TryApply(0,_=>throw new Exception("Stale apply reached callback"),()=>false)&&!session.Ready,"Stale source blocks apply");
            var completion=new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending=session.RenderAsync(3,1,(ev,token)=>{started.TrySetResult();return completion.Task;},()=>true,CancellationToken.None);
            await started.Task;session.Invalidate();completion.SetResult(Frame(99));
            try{await pending;throw new Exception("Cancelled late frame was published");}catch(OperationCanceledException){checks++;}
            Check(!session.Ready&&session.Results.Length==0,"Cancelled result cannot be applied");
            var late=new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);var firstStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var obsolete=session.RenderAsync(3,1,(_,_)=>{firstStarted.TrySetResult();return late.Task;},()=>true,CancellationToken.None);
            await firstStarted.Task;int newerCalls=0;
            var replacement=session.RenderAsync(5,.5,(_,_)=>{newerCalls++;return Task.FromResult(Frame(73));},()=>true,CancellationToken.None);
            Check(newerCalls==0,"Replacement waits for the previous callback to release the renderer");late.SetResult(Frame(12));
            try{await obsolete;throw new Exception("Obsolete render completed");}catch(OperationCanceledException){checks++;}
            await replacement;Check(session.Ready&&session.Results.Length==5&&newerCalls==5,"Latest generation publishes a complete independent comparison");
            Check(!session.TryApply(2,_=>false,()=>true),"Rejected Apply reports failure");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{await session.RenderAsync(3,1,(_,_)=>throw new Exception("Cancelled render started"),()=>true,cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){checks++;}
            Console.WriteLine($"PASS {checks} assertions; no UI or C1 opened");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
