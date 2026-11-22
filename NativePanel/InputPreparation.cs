using System;
using System.IO;
using System.Linq;
using ImgCore;

internal enum SourcePreparation { CaptureOne, NeutralFilm }

internal static class InputPreparation
{
    // IC_ProcessSettings and its local-adjustment inline array are value types.
    // Replace strings and scalar fields only: never modify shared handles/arrays.
    internal static IC_ProcessSettings Prepare(IC_ProcessSettings settings)
    {
        var curve = ResolveLinearCurve(settings.filmCurve.Value);
        settings.filmCurve = Utf8Marshaled_IC_MAX_PATH.Create(curve);
        settings.colorFilmCurveRef = IntPtr.Zero; // Do not reuse the previously resolved Auto/film curve.
        settings.imageProcessSettings.USMAmount = 0;
        settings.imageProcessSettings.clarityParams.amount = 0;
        settings.imageProcessSettings.clarityParams.structureAmount = 0;
        settings.outputSharpeningAmount = 0;
        settings.printSharpening.amount = 0;
        for (var i = 0; i < 16; i++)
        {
            settings.localAdjust[i].localSettings.USMAmount = 0;
            settings.localAdjust[i].localSettings.clarityParams.amount = 0;
            settings.localAdjust[i].localSettings.clarityParams.structureAmount = 0;
        }
        return settings;
    }

    internal static string ResolveLinearCurve(string currentCurve)
    {
        var name = Path.GetFileName(currentCurve);
        var folder = Path.Combine(Path.GetDirectoryName(typeof(IC_ProcessSettings).Assembly.Location)!, "Film Curves");
        const string suffix = "-Linear Response.fcrv";
        // Match the complete camera prefix; choose the longest to avoid a shorter
        // model prefix accidentally matching a different camera.
        var match = Directory.EnumerateFiles(folder, "*" + suffix)
            .Where(path => name.StartsWith(Path.GetFileName(path)[..^suffix.Length] + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path).Length).FirstOrDefault();
        return match ?? throw new InvalidOperationException(
            "A matching Capture One Linear Response curve is unavailable. Choose 'Capture One development' in Input preparation for this image.");
    }
}
