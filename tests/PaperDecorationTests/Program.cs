using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
internal static class Program
{
    private static int checks,failures;private static double maximumError;
    private static readonly string artifacts=Path.GetFullPath(Path.Combine("artifacts/full-function-20261116/paper-decoration",Guid.NewGuid().ToString("N")));
    private static void Check(bool yes,string reason){checks++;if(!yes)throw new Exception(reason);}
    private static void Test(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e);}}
    private static void Reject<T>(Action action)where T:Exception{try{action();}catch(T){checks++;return;}throw new Exception("Expected "+typeof(T).Name);}
    private static string Id(int i)=>new((char)('a'+i),64);
    [STAThread] private static int Main()
    {
        Directory.CreateDirectory(artifacts);
        Test("Exact reference export oracle: six modes, opacity, ordered layers, nearest fit and padding",Oracle);
        Test("Neutral memory identity, preserved HDR/alpha, texture immutability and validation",Neutral);
        Test("Serialized per-look state, stable identities and malformed state rejection",Storage);
        Test("Texture import, immutable hash cache, missing-source independence and RGB16/ICC export",ImportExport);
        File.WriteAllText(Path.Combine(artifacts,"verification.json"),JsonSerializer.Serialize(new{checks,failures,maximumError},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{checks} checks; {failures} failures; max reference float error {maximumError:E6}; {artifacts}");return failures==0?0:1;
    }
    private static void Oracle()
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")));var data=json.RootElement;
        var textures=new Dictionary<string,PaperTexture>();int index=0;
        foreach(var tex in data.GetProperty("textures").EnumerateArray())textures.Add(Id(index++),new(tex.GetProperty("width").GetInt32(),tex.GetProperty("height").GetInt32(),tex.GetProperty("rgb").EnumerateArray().Select(x=>x.GetByte()).ToArray()));
        foreach(var item in data.GetProperty("cases").EnumerateArray())
        {
            var image=data.GetProperty("images")[item.GetProperty("image").GetInt32()];int width=image.GetProperty("width").GetInt32(),height=image.GetProperty("height").GetInt32();
            var rgba=image.GetProperty("rgba").EnumerateArray().Select(x=>x.GetSingle()).ToArray();var copy=(float[])rgba.Clone();
            var layers=item.GetProperty("layers").EnumerateArray().Select(x=>new PaperTextureLayer(Id(x[0].GetInt32()),PaperDecoration.ParseDwMode(x[1].GetString()),x[2].GetDouble())).ToImmutableArray();
            var settings=new PaperDecorationSettings(item.GetProperty("active").GetBoolean(),item.GetProperty("burn").GetBoolean(),item.GetProperty("fraction").GetDouble(),layers);
            var result=PaperDecoration.ApplyRgba(rgba,width,height,settings,id=>textures[id],item.GetProperty("padding").GetBoolean());
            Check(result.Width==item.GetProperty("width").GetInt32()&&result.Height==item.GetProperty("height").GetInt32(),"Wrong output dimensions");
            int pad=item.GetProperty("pad").GetInt32();Check(result.Content==new PaperContentRect(pad,pad,width,height),"Content coordinates differ");
            var expected=item.GetProperty("expected");for(int i=0;i<result.Pixels.Length;i++)
            {double difference=Math.Abs(result.Pixels.Span[i]-expected[i].GetDouble());maximumError=Math.Max(maximumError,difference);Check(difference<=2e-7,"reference pixel mismatch: "+difference);}
            Check(rgba.SequenceEqual(copy),"Composition modified the source image");
        }
    }
    private static void Neutral()
    {
        var source=new float[]{float.NaN,-0f,2f,.4f};var memory=new ReadOnlyMemory<float>(source);
        var layer=new PaperTextureLayer(Id(0));
        foreach(var settings in new[]{new PaperDecorationSettings(),new(Active:true,PaddingBurn:true),new(Active:false,Layers:ImmutableArray.Create(layer)),new(Active:true,Layers:ImmutableArray.Create(layer with{Opacity=0}))})
        {var result=PaperDecoration.ApplyRgba(memory,1,1,settings,_=>throw new Exception("Neutral loaded a texture"));Check(result.Pixels.Equals(memory),"Neutral copied/changed pixels");Check(BitConverter.SingleToInt32Bits(result.Pixels.Span[1])==BitConverter.SingleToInt32Bits(-0f),"Signed zero changed");}
        var textureBytes=new byte[]{127,255,0};var texture=new PaperTexture(1,1,textureBytes);textureBytes[0]=0;Check(texture.Rgb.Span[0]==127,"Texture did not own its immutable bytes");
        var active=new PaperDecorationSettings(Active:true,Layers:ImmutableArray.Create(layer));
        Reject<ArgumentException>(()=>PaperDecoration.ApplyRgba(source,1,1,active,_=>texture));
        Reject<OperationCanceledException>(()=>PaperDecoration.ApplyRgba(source,1,1,active,_=>texture,token:new(true)));
        Reject<ArgumentException>(()=>new PaperDecorationSettings(PaddingFraction:double.NaN).Validate());
        Reject<ArgumentException>(()=>new PaperDecorationSettings(Layers:ImmutableArray.Create(layer with{AssetId="../file"})).Validate());
        Reject<ArgumentException>(()=>new PaperDecorationSettings(Layers:ImmutableArray.Create(layer with{Opacity=1.1})).Validate());
        // An extreme border must reject before loading textures or allocating
        // the enlarged image; never silently downsample export resolution.
        Reject<ArgumentException>(()=>PaperDecoration.ApplyRgba(new float[9000*4],9000,1,active with{PaddingBurn=true,PaddingFraction=1},_=>throw new Exception("Resolved before limits")));
        var hdr=new float[]{-.2f,1.8f,.3f,.7f};var padded=PaperDecoration.ApplyRgba(hdr,1,1,active with{PaddingBurn=true,PaddingFraction=1,Layers=ImmutableArray.Create(layer with{Opacity=0})},_=>throw new Exception("Zero opacity loaded asset"));
        Check(padded.Width==3&&padded.Height==3,"Padding-only geometry");for(int c=0;c<4;c++)Check(padded.Pixels.Span[(1*3+1)*4+c]==hdr[c],"Padding clamped the photograph");
        Check(padded.Pixels.Span[3]==1,"Padding alpha is not opaque");
    }
    private static void Storage()
    {
        var settings=new PaperDecorationSettings(true,true,.07,ImmutableArray.Create(new PaperTextureLayer(Id(0),PaperBlendMode.SoftLight,.35,"paper.png"),new PaperTextureLayer(Id(1),PaperBlendMode.Screen,.2,"second.png")));
        string encoded=PaperDecorationStorage.Serialize(settings);Check(ReferenceEquals(settings,PaperDecorationStorage.Parse(encoded)),"Parsed settings identity was not cached");
        Check(PaperDecorationStorage.Serialize(PaperDecorationStorage.Parse(encoded))==encoded,"Serialization is unstable");
        var first=new Dictionary<string,string>{{"other","kept"}};var second=new Dictionary<string,string>();PaperDecorationStorage.Capture(settings,first);PaperDecorationStorage.Capture(new(),second);
        Check(first["other"]=="kept"&&PaperDecorationStorage.FromSettings(first).Layers.Length==2,"Capture changed unrelated selection");
        Check(PaperDecorationStorage.FromSettings(second).IsIdentity,"One variant changed another");Check(ReferenceEquals(PaperDecorationStorage.FromSettings(null),PaperDecorationSettings.Default),"Missing old settings not neutral");
        Reject<FormatException>(()=>PaperDecorationStorage.Parse(encoded.Replace("Soft Light","nonsense")));
        Reject<FormatException>(()=>PaperDecorationStorage.Parse("{}"));Reject<FormatException>(()=>PaperDecorationStorage.Parse(encoded.Replace("\"version\":1","\"version\":2")));
        Check(first[PaperDecorationStorage.SelectionKey]==encoded,"Rejected data changed saved selection");
    }
    private static void Png(string path,BitmapSource image,ColorContext? profile=null)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(profile is null?BitmapFrame.Create(image):BitmapFrame.Create(image,null,null,new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>(new[]{profile})));
        using var file=File.Create(path);encoder.Save(file);
    }
    private static void ImportExport()
    {
        var store=new PaperTextureStore(Path.Combine(artifacts,"reference"));const int width=3,height=2;
        byte[] rgb=[12,91,201,45,200,19,213,32,78,55,123,254,180,83,27,255,0,150];
        var bgra=new byte[width*height*4];for(int p=0;p<width*height;p++){bgra[p*4]=rgb[p*3+2];bgra[p*4+1]=rgb[p*3+1];bgra[p*4+2]=rgb[p*3];bgra[p*4+3]=(byte)(p*51);}
        string source=Path.Combine(artifacts,"source.png"),duplicate=Path.Combine(artifacts,"different-container.png");
        Png(source,BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,bgra,width*4));
        Png(duplicate,BitmapSource.Create(width,height,96,96,PixelFormats.Rgb24,null,rgb,width*3));
        var asset=store.Import(source);var same=store.Import(duplicate);Check(asset.AssetId==same.AssetId,"Normalization includes texture alpha/container metadata");
        var texture=store.Load(asset.AssetId);Check(texture.Rgb.Span.SequenceEqual(rgb),"Importer channel order or alpha handling");Check(ReferenceEquals(texture,store.Load(asset.AssetId)),"Immutable texture cache missed");
        Check(asset.Name=="source.png"&&asset.Width==3&&asset.Height==2,"Import descriptor");
        var settings=new PaperDecorationSettings(true,true,.2,ImmutableArray.Create(new PaperTextureLayer(asset.AssetId,PaperBlendMode.SoftLight,.35,asset.Name)));
        string persisted=PaperDecorationStorage.Serialize(settings);Check(!persisted.Contains(artifacts),"Saved state leaked a source path");
        File.Delete(source);File.Delete(duplicate); // our synthetic fixtures only
        var restored=PaperDecorationStorage.Parse(persisted);var reopened=new PaperTextureStore(Path.Combine(artifacts,"reference"));
        var frame=Enumerable.Range(0,7*5).SelectMany(i=>new float[]{i/35f,.1234567f,.8f,(i%11)/10f}).ToArray();
        var result=PaperDecoration.ApplyRgba(frame,7,5,restored,id=>reopened.Load(id));Check(result.Width==9&&result.Height==7,"Restored border dimensions");
        var codes=new ushort[result.Pixels.Length];for(int i=0;i<codes.Length;i++)codes[i]=(ushort)Math.Round(Math.Clamp((double)result.Pixels.Span[i],0,1)*65535);
        Check(codes.Any(x=>x%257!=0),"Paper export collapsed the photo to 8-bit");
        var bitmap=BitmapSource.Create(result.Width,result.Height,96,96,PixelFormats.Rgba64,null,codes,result.Width*8);
        string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"spool","drivers","color","sRGB Color Space Profile.icm");
        string output=Path.Combine(artifacts,"restored-paper-rgba16.png");Png(output,bitmap,new ColorContext(new Uri(profile)));
        using(var file=File.OpenRead(output))
        {
            var decoded=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
            var actual=new ushort[codes.Length];decoded.CopyPixels(actual,result.Width*8,0);Check(actual.SequenceEqual(codes)&&decoded.Format==PixelFormats.Rgba64,"RGB16/alpha export changed");
            using var icc=decoded.ColorContexts![0].OpenProfileStream();using var bytes=new MemoryStream();icc.CopyTo(bytes);Check(bytes.ToArray().SequenceEqual(File.ReadAllBytes(profile)),"Paper changed ICC");
        }
        Reject<FileNotFoundException>(()=>store.Load(new string('f',64)));Reject<ArgumentException>(()=>store.Load("../not-a-hash"));
        string huge=Path.Combine(artifacts,"huge.png");Png(huge,BitmapSource.Create(8193,1,96,96,PixelFormats.Rgb24,null,new byte[8193*3],8193*3));
        Reject<InvalidDataException>(()=>store.Import(huge));Reject<OperationCanceledException>(()=>store.Import(huge,new(true)));
        string large=Path.Combine(artifacts,"too-large.bin");using(var file=File.Create(large))file.SetLength(PaperTextureStore.MaximumImportBytes+1);
        Reject<InvalidDataException>(()=>store.Import(large));File.Delete(large);
        string stored=Path.Combine(store.DirectoryPath,asset.AssetId+".sfptex");var corrupted=File.ReadAllBytes(stored);corrupted[^1]^=1;File.WriteAllBytes(stored,corrupted);File.SetLastWriteTimeUtc(stored,DateTime.UtcNow.AddSeconds(1));
        Reject<InvalidDataException>(()=>store.Load(asset.AssetId));Reject<InvalidDataException>(()=>reopened.Load(asset.AssetId));
        Check(!Directory.EnumerateFiles(store.DirectoryPath,"*.tmp").Any(),"Import leaked an incomplete asset");
    }
}
