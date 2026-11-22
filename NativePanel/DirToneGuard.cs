using System.Globalization;

// Strong DIR couplers make the coupler model fold: more exposure produces more inhibition and less
// effective exposure, so darker tones can print brighter. Measured 2026-11-18 on all 26 films with a
// neutral -10..+4 EV ramp through the renderer (docs/FLX-REVIEW-20261118.md): every setting with an
// effective strength up to 1.5 was monotonic, and every reversing setting was at 1.56 or more.
// The panel warns; it does not change the values or the render.
internal static class DirToneGuard
{
    internal const double Limit = 1.5;

    /// <summary>DIR coupler amount weighted by both inhibitions; equal to the amount at the default 1 / 1.</summary>
    internal static double EffectiveStrength(double amount, double sameLayer, double interlayer) =>
        amount * (0.4 * sameLayer + 0.6 * interlayer);

    /// <summary>The warning to show, or null when tones stay in order.</summary>
    internal static string? Warning(double amount, double sameLayer, double interlayer)
    {
        var strength = EffectiveStrength(amount, sameLayer, interlayer);
        if (!(strength > Limit))
            return null;
        return string.Format(CultureInfo.InvariantCulture,
            "Strong DIR couplers (effective strength {0:0.00}, above {1:0.0}) can reverse tones: on some films darker areas print brighter, "
            + "most visibly on slide films (Velvia, Provia) and Fujifilm C200 / X-Tra 400 or pushed Portra 800. "
            + "Lower DIR coupler amount or the inhibitions to keep tones in order.", strength, Limit);
    }
}
