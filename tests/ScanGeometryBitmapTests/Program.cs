using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int checks;private static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    [STAThread] private static int Main()
    {
        try
        {
            const int w=17,h=11;var random=new Random(17342);var bytes=new byte[w*h*4];random.NextBytes(bytes);
            var image=BitmapSource.Create(w,h,120,120,PixelFormats.Bgra32,null,bytes,w*4);image.Freeze();
            Check(ReferenceEquals(ScanGeometryBitmap.Apply(image,new()).Bitmap,image),"Neutral bitmap replaced");
            var settings=new ScanGeometrySettings(Desqueeze:1.33,SkewDegrees:-15,Crop:true,CenterX:.7,CenterY:.3,SizeX:.6,SizeY:.4);
            var bitmap=ScanGeometryBitmap.Apply(image,settings);var rgba=new float[bytes.Length];
            for(int i=0;i<rgba.Length;i+=4){rgba[i]=bytes[i+2]/255f;rgba[i+1]=bytes[i+1]/255f;rgba[i+2]=bytes[i]/255f;rgba[i+3]=bytes[i+3]/255f;}
            var direct=ScanGeometry.ApplyRgba(rgba,w,h,settings);var pixels=new byte[direct.Pixels.Length];bitmap.Bitmap.CopyPixels(pixels,direct.Width*4,0);
            Check(bitmap.Bitmap.IsFrozen&&bitmap.Bitmap.DpiX==120&&bitmap.Bitmap.DpiY==120,"Frozen/DPI contract");
            Check(bitmap.Bitmap.PixelWidth==direct.Width&&bitmap.Bitmap.PixelHeight==direct.Height,"BGRA dimensions");
            for(int i=0;i<pixels.Length;i++)Check(pixels[i]==(byte)Math.Round(direct.Pixels.Span[i%4==0?i+2:i%4==2?i-2:i]*255d),"BGRA channel/alpha mapping");
            var unchanged=new byte[bytes.Length];image.CopyPixels(unchanged,w*4,0);Check(unchanged.SequenceEqual(bytes),"Original bitmap modified");
            // Geometry runs before 16-bit packing. All four target ICC profiles
            // survive the actual lossless writers with the new width/height.
            var artifact=Path.GetFullPath(Path.Combine("artifacts/full-function-20261116/scan-geometry",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(artifact);
            for(int i=0;i<rgba.Length;i+=4){rgba[i]=(float)(random.NextDouble()*1.8-.3);rgba[i+1]=(float)random.NextDouble();rgba[i+2]=(float)random.NextDouble();rgba[i+3]=(float)random.NextDouble();}
            foreach(var space in Enum.GetValues<ExportRgbSpace>().Where(x=>x is not (ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.VGamut)))
            {
                var encoded=(float[])rgba.Clone();for(int i=0;i<encoded.Length;i+=4){var c=ExportColorSpace.FromLinearSrgb(encoded[i],encoded[i+1],encoded[i+2],space);encoded[i]=(float)c.Red;encoded[i+1]=(float)c.Green;encoded[i+2]=(float)c.Blue;}
                var geometry=ScanGeometry.ApplyRgba(encoded,w,h,settings);var target=geometry.Pixels.Span;var profile=ExportColorSpace.ResolveProfile(space,Path.GetFullPath("."));
                foreach(bool alpha in new[]{false,true})
                {
                    int channels=alpha?4:3;var codes=new ushort[geometry.Width*geometry.Height*channels];
                    for(int p=0;p<geometry.Width*geometry.Height;p++)for(int c=0;c<channels;c++)codes[p*channels+c]=(ushort)Math.Round(Math.Clamp((double)target[p*4+c],0,1)*65535);
                    var packed=BitmapSource.Create(geometry.Width,geometry.Height,96,96,alpha?PixelFormats.Rgba64:PixelFormats.Rgb48,null,codes,geometry.Width*channels*2);packed.Freeze();
                    var path=Path.Combine(artifact,space+(alpha?".png":".tif"));SpektraExport.SaveWithProfile(path,packed,default,profile);
                    using var file=File.OpenRead(path);var decoded=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                    Check(decoded.PixelWidth==geometry.Width&&decoded.PixelHeight==geometry.Height,"Export retained pre-geometry dimensions");
                    var restored=new ushort[codes.Length];decoded.CopyPixels(restored,geometry.Width*channels*2,0);Check(restored.SequenceEqual(codes),"Geometry output lost RGB16/alpha precision");Check(codes.Any(c=>c%257!=0),"Unexpected 8-bit quantization");
                    using var icc=decoded.ColorContexts![0].OpenProfileStream();using var memory=new MemoryStream();icc.CopyTo(memory);Check(memory.ToArray().SequenceEqual(File.ReadAllBytes(profile)),"Geometry changed target ICC");
                }
            }
            Console.WriteLine($"PASS {checks} BGRA/RGB16/alpha/ICC assertions; no C1/UI; {artifact}");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
