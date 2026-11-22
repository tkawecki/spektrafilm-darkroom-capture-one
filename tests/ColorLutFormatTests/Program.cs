using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
var folder=Path.GetFullPath("artifacts/color-lut-formats");Directory.CreateDirectory(folder);int checks=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
foreach(var n in new[]{33,64,65}){
 var input=MemoryMarshal.Cast<byte,float>(NativeColorLut.CreateGrid(n).AsSpan());var rgb=new double[n*n*n*3];
 for(int i=0;i<rgb.Length/3;i++){rgb[i*3]=input[i*4]*.8;rgb[i*3+1]=Math.Pow(input[i*4+1],.8);rgb[i*3+2]=1-input[i*4+2];}
 var cube=NativeColorLut.Encode(n,rgb);var three=ColorLutFiles.ThreeDl(cube);var lines=three.Split('\n').Where(x=>x.Length>0&&!x.StartsWith('#')).ToArray();
 Check(lines.Length==n*n*n+1,"3dl rows");var nodes=lines.Skip(1).SelectMany(x=>x.Split(' ').Select(int.Parse)).ToArray();
 for(int i=0;i<rgb.Length;i++)Check(nodes[i]==(int)Math.Round(rgb[i]*1023),"3dl quantization/order");
 File.WriteAllText(Path.Combine(folder,$"look-{n}.3dl"),three);
 File.WriteAllText(Path.Combine(folder,$"look-{n}.cube"),cube);
 if(n==64){ColorLutFiles.SaveHald(Path.Combine(folder,"hald8.png"),cube);var bitmap=ColorLutFiles.Hald(cube);var bytes=new byte[512*512*3];bitmap.CopyPixels(bytes,512*3,0);for(int i=0;i<rgb.Length;i++)Check(bytes[i]==(byte)Math.Round(rgb[i]*255),"Hald quantization/order");}
 if(n==33){File.WriteAllText(Path.Combine(folder,"config.ocio"),ColorLutFiles.OcioConfig);File.WriteAllText(Path.Combine(folder,"look.cube"),cube);}
}
Console.WriteLine($"PASS {checks} LUT format checks; artifacts: {folder}");
File.WriteAllText(Path.Combine(folder,"encoded.ocio"),ColorLutFiles.OcioForInput("S-Log3 / S-Gamut3.Cine"));
