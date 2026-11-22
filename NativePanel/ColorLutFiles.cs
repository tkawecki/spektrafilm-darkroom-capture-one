// SPDX-License-Identifier: GPL-3.0-only
// .3dl/Hald layout and quantization follow The Dyeworks 2.2.3
// spektrafilm_lut_creator/formats (Andrea Volpato / Celian Le Bolloch).
// OCIO config below explicitly describes this adapter's encoded-sRGB cube.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ColorLutFiles
{
    internal sealed record Grid(int Size, double[] Values, string[] Comments);
    internal static Grid Parse(string cube)
    {
        var lines = cube.Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
        var declaration=lines.Single(x=>x.StartsWith("LUT_3D_SIZE ",StringComparison.Ordinal));
        var size=int.Parse(declaration.Split(' ',StringSplitOptions.RemoveEmptyEntries)[1],CultureInfo.InvariantCulture);
        if(size is not (33 or 64 or 65))throw new InvalidDataException("Unsupported color LUT size.");
        var values=lines.Where(x=>char.IsAsciiDigit(x[0])||x[0] is '-' or '+')
            .SelectMany(x=>x.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,CultureInfo.InvariantCulture))).ToArray();
        if(values.Length!=size*size*size*3||values.Any(x=>!double.IsFinite(x)||x<0||x>1))throw new InvalidDataException("Invalid color LUT samples.");
        return new(size,values,lines.Where(x=>x.StartsWith('#')).ToArray());
    }
    internal static string ThreeDl(string cube)
    {
        var grid=Parse(cube);var text=new StringBuilder();
        foreach(var comment in grid.Comments)text.AppendLine(comment);
        text.AppendLine("# Autodesk/Discreet 10-bit; red index varies fastest.");
        text.AppendLine(string.Join(" ",Enumerable.Range(0,grid.Size).Select(i=>Math.Round(1023d*i/(grid.Size-1)).ToString(CultureInfo.InvariantCulture))));
        for(var i=0;i<grid.Values.Length;i++)text.Append(Math.Round(grid.Values[i]*1023).ToString(CultureInfo.InvariantCulture)).Append(i%3==2?'\n':' ');
        return text.ToString();
    }
    internal static BitmapSource Hald(string cube)
    {
        var grid=Parse(cube);if(grid.Size!=64)throw new InvalidDataException("Hald level 8 requires a directly rendered 64-point cube.");
        const int side=512;var pixels=grid.Values.Select(x=>(byte)Math.Round(x*255)).ToArray();
        var bitmap=BitmapSource.Create(side,side,96,96,PixelFormats.Rgb24,null,pixels,side*3);bitmap.Freeze();return bitmap;
    }
    internal static void SaveHald(string path,string cube)
    {
        var image=Hald(cube);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        PanelFiles.WriteAtomically(path,encoder.Save);
    }
    internal static string OcioConfig => """
ocio_profile_version: 2
description: |
  Spektrafilm C1 color-only look. Cube input/output is encoded sRGB.
  Scene reference is linear Rec.709/D65, not ACES. Spatial effects and
  image-dependent exposure are excluded. Input outside 0..1 is LUT-clamped.
search_path: .
strictparsing: true
roles:
  default: sRGB
  scene_linear: Linear Rec.709
  reference: Linear Rec.709
  data: Raw
displays:
  Spektrafilm:
    - !<View> {name: Film look, colorspace: Spektrafilm look}
    - !<View> {name: sRGB, colorspace: sRGB}
active_displays: [Spektrafilm]
active_views: [Film look, sRGB]
colorspaces:
  - !<ColorSpace>
    name: Linear Rec.709
    family: input
    bitdepth: 32f
    isdata: false
    allocation: uniform
  - !<ColorSpace>
    name: sRGB
    family: input
    bitdepth: 32f
    isdata: false
    to_scene_reference: !<ExponentWithLinearTransform> {gamma: [2.4, 2.4, 2.4, 1], offset: [0.055, 0.055, 0.055, 0]}
  - !<ColorSpace>
    name: Raw
    bitdepth: 32f
    isdata: true
  - !<ColorSpace>
    name: Spektrafilm look
    family: display
    bitdepth: 32f
    isdata: false
    from_scene_reference: !<GroupTransform>
      children:
        - !<ExponentWithLinearTransform> {gamma: [2.4, 2.4, 2.4, 1], offset: [0.055, 0.055, 0.055, 0], direction: inverse}
        - !<FileTransform> {src: look.cube, interpolation: tetrahedral}
""";
    internal static string OcioForInput(string input)
    {
        if(input=="srgb")return OcioConfig;
        var encoded=System.Text.Json.JsonSerializer.Serialize(input);
        return """
ocio_profile_version: 2
description: |
  Spektrafilm C1 look for already encoded input. The reference contains
  code values, not scene-linear light. No implicit ACES or camera conversion.
search_path: .
strictparsing: true
roles:
  default: Input code
  reference: Input code
  data: Raw
displays:
  Spektrafilm:
    - !<View> {name: Film look, colorspace: Spektrafilm look}
active_displays: [Spektrafilm]
active_views: [Film look]
colorspaces:
  - !<ColorSpace>
    name: Input code
    family: encoded input
    description: ENCODING
    bitdepth: 32f
    isdata: false
    allocation: uniform
  - !<ColorSpace>
    name: Raw
    bitdepth: 32f
    isdata: true
  - !<ColorSpace>
    name: Spektrafilm look
    family: sRGB display
    bitdepth: 32f
    isdata: false
    from_scene_reference: !<FileTransform> {src: look.cube, interpolation: tetrahedral}
""".Replace("ENCODING",encoded,StringComparison.Ordinal);
    }
    internal static string SaveBundle(string parent,string cube,string input="srgb")
    {
        _=Parse(cube);
        var folder=Path.Combine(parent,"Spektrafilm-"+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"look.cube"),cube,new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder,"config.ocio"),OcioForInput(input),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder,"README.txt"),(input=="srgb" ? "Input: encoded sRGB or linear Rec.709. Use the matching named input space."
            : "Input: "+input+" code values in 0..1. Choose Input code. No implicit conversion from ACES/linear/another camera encoding.")
            +"\nOutput: sRGB film look. The cube contains only pointwise color/tone. See look.cube for scope and attribution.\n",new UTF8Encoding(false));
        return folder;
    }
}
