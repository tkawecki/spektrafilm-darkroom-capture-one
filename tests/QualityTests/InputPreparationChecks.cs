using System;
using ImgCore;
internal static class InputPreparationChecks
{
    internal static void Run()
    {
        var original = new IC_ProcessSettings { filmCurve = Utf8Marshaled_IC_MAX_PATH.Create("CanonEOS5DSR-Auto.fcrv"), colorFilmCurveRef = (IntPtr)1234 };
        original.imageProcessSettings.USMAmount = 140;
        original.imageProcessSettings.clarityParams.amount = 23;
        original.imageProcessSettings.clarityParams.structureAmount = -12;
        original.imageProcessSettings.exposure = .75f;
        original.printSharpening.amount = 10;
        original.outputSharpeningAmount = 7;
        foreach (var i in new[] { 0, 15 })
        {
            original.localAdjust[i].localSettings.USMAmount = 38 + i;
            original.localAdjust[i].localSettings.clarityParams.amount = 20;
            original.localAdjust[i].localSettings.clarityParams.structureAmount = 30;
            original.localAdjust[i].localSettings.exposure = .264f;
            original.localAdjust[i].localSettings.opacity = .6f;
        }
        var neutral = (IC_ProcessSettings)Program.Invoke("InputPreparation", "Prepare", original);
        Require(original.imageProcessSettings.USMAmount == 140 && original.imageProcessSettings.clarityParams.amount == 23
            && original.imageProcessSettings.clarityParams.structureAmount == -12 && original.colorFilmCurveRef == (IntPtr)1234,
            "Original global fields modified");
        Require(neutral.imageProcessSettings.USMAmount == 0 && neutral.imageProcessSettings.clarityParams.amount == 0
            && neutral.imageProcessSettings.clarityParams.structureAmount == 0 && neutral.colorFilmCurveRef == IntPtr.Zero
            && neutral.outputSharpeningAmount == 0 && neutral.printSharpening.amount == 0, "Neutral global fields incorrect");
        foreach (var i in new[] { 0, 15 })
        {
            var before = original.localAdjust[i].localSettings; var after = neutral.localAdjust[i].localSettings;
            Require(before.USMAmount == 38+i && before.clarityParams.amount == 20 && before.clarityParams.structureAmount == 30,
                "Original local adjustment changed");
            Require(after.USMAmount == 0 && after.clarityParams.amount == 0 && after.clarityParams.structureAmount == 0
                && after.exposure == before.exposure && after.opacity == before.opacity, "Neutral local adjustment incorrect");
        }
        Require(original.filmCurve.Value == "CanonEOS5DSR-Auto.fcrv", "Original curve changed");
        var unsupported = original;
        unsupported.filmCurve = Utf8Marshaled_IC_MAX_PATH.Create("UnrecognisedCamera-Auto.fcrv");
        try { Program.Invoke("InputPreparation", "Prepare", unsupported); throw new Exception("Unsupported curve silently accepted"); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
        Require(unsupported.localAdjust[15].localSettings.USMAmount == 53 && unsupported.imageProcessSettings.USMAmount == 140,
            "Failure changed input");
    }
    private static void Require(bool result, string message) { if (!result) throw new Exception(message); }
}
