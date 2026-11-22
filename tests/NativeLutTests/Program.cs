using System.Globalization;
using System.Runtime.InteropServices;

static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
foreach (var size in new[] {33, 65}) {
    var grid = NativeColorLut.CreateGrid(size);
    var data = MemoryMarshal.Cast<byte,float>(grid.AsSpan());
    Check(data.Length == size*size*size*4, "Grid dimensions");
    for(int b=0;b<size;b++) for(int g=0;g<size;g++) for(int r=0;r<size;r++) {
        int i=((b*size+g)*size+r)*4;
        Check(data[i]==(float)r/(size-1) && data[i+1]==(float)g/(size-1) && data[i+2]==(float)b/(size-1) && data[i+3]==1, "red-fastest order");
    }
    var rgb = new double[size*size*size*3];
    for(int i=0;i<rgb.Length/3;i++) for(int c=0;c<3;c++) rgb[i*3+c]=data[i*4+c];
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
    var cube = NativeColorLut.Encode(size, rgb);
    Check(cube.Contains("LUT_3D_SIZE "+size) && cube.Contains("adaptive exposure") && cube.Contains("spatial"), "Scope missing");
    var rows=cube.Split('\n').Where(x=>x.Length>0 && char.IsAsciiDigit(x[0])).ToArray();
    Check(rows.Length==size*size*size,"Cube node count");
    for(int i=0;i<rows.Length;i++) {
        var parsed=rows[i].Split(' ').Select(x=>double.Parse(x,CultureInfo.InvariantCulture)).ToArray();
        Check(parsed.Length==3 && Enumerable.Range(0,3).All(c=>Math.Abs(parsed[c]-rgb[i*3+c])<1e-9),"Cube values");
    }
}
var original = new[]{"--stock","kodak_portra_400","--auto-exposure","true","--scanner-white-level","0.9","--grain-model","synthesis","--input-color","prophoto","--halation-amount","4"};
var options=NativeColorLut.ColorOnlyOptions(original);
var map=Enumerable.Range(0,options.Length/2).ToDictionary(i=>options[2*i],i=>options[2*i+1]);
Check(map["--stock"]=="kodak_portra_400" && map["--scanner-white-level"]=="0.9","Pointwise settings lost");
Check(map["--auto-exposure"]=="false" && map["--grain-model"]=="off" && map["--input-color"]=="srgb" && map["--halation-amount"]=="0","Adaptive/spatial not excluded");
Check(original[3]=="true","Caller arguments mutated");
try { NativeColorLut.Encode(33,new double[3]); throw new Exception("Malformed accepted"); } catch(ArgumentException) {}
var invalid=new double[33*33*33*3]; invalid[99]=double.NaN;
try { NativeColorLut.Encode(33,invalid); throw new Exception("NaN accepted"); } catch(InvalidDataException) {}
Console.WriteLine("PASS: 33/65 sRGB grid, exact node order, serialization/culture, scope, option immutability and invalid data");
