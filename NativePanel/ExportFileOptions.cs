using System;
using System.IO;
using System.Windows.Media;

// Image containers are separate from the renderer's precision and encoding.
internal static class ExportFileOptions
{
    internal const string Filter = "16-bit TIFF image|*.tif|16-bit PNG image|*.png|JPEG image (8-bit)|*.jpg|Linear RGB DNG (16-bit)|*.dng";
    internal static string ExtensionFor(string? value) => value?.ToLowerInvariant() switch {
        "png" or ".png" => ".png", "jpeg" or "jpg" or ".jpg" or ".jpeg" => ".jpg",
        "dng" or ".dng" => ".dng", _ => ".tif",
    };
    internal static int FilterIndex(string extension) => extension switch { ".png"=>2, ".jpg"=>3, ".dng"=>4, _=>1 };
    internal static bool IsDng(string path) => Path.GetExtension(path).Equals(".dng",StringComparison.OrdinalIgnoreCase);
    internal static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
    internal static PixelFormat WorkingFormat(string path) => IsJpeg(path)?PixelFormats.Rgb48:SpektraExport.PixelFormatFor(path);
}
