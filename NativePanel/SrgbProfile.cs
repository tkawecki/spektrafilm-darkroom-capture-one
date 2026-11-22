using System;
using System.IO;
using System.Windows.Media;

// The sRGB profile of Viewer pixels: the source of the monitor conversion, the profile embedded in a saved
// preview PNG, and the reference for "this monitor is sRGB". It is Windows' sRGB IEC61966-2.1 file, the
// one sRGB exports embed. WPF's ColorContext(PixelFormats.Bgra32) is not used: it returns the system's
// standard-sRGB setting, which colour tools can redirect. On 2026-11-18 it returned an Eizo
// "CX271 ... Paper" profile here, so the Viewer was converted from the wrong source.
internal static class SrgbProfile
{
    // The same file as ExportColorSpace.ResolveProfile(ExportRgbSpace.Srgb).
    private static readonly Lazy<string> path = new(() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
    private static readonly Lazy<byte[]> bytes = new(() => File.ReadAllBytes(Path));

    internal static string Path => path.Value;
    internal static ReadOnlySpan<byte> Bytes => bytes.Value;
    internal static ColorContext Context() => new(new Uri(Path, UriKind.Absolute));
}
