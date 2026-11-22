internal static class QualityDefaults
{
    // Missing values in older/sparse presets keep the defaults they had when saved.
    internal static double NumberFallback(string key, double baseline, bool current) =>
        key == "dir-diffusion" ? (current ? 0 : 20)
        // Default lowered to 2 EV on 2026-11-17 together with relative protection; earlier looks used 4 EV.
        : key == "halation-protect" ? 4
        : baseline;

    internal static bool SwitchFallback(string key, bool baseline, bool current) =>
        key is "color-adaptation" or "auto-exposure" ? current
        // Added 2026-11-17: looks saved before it existed keep the original grain behaviour.
        : key is "grain-mean-restore" or "halation-protect-relative" ? false
        // Added 2026-11-18: looks saved before it existed keep point-sampled Grain synthesis.
        : key is "grain-synthesis-aperture" ? false
        // Added 2026-11-19: looks saved before it existed keep Monte Carlo sampling noise.
        : key is "grain-synthesis-noise-match" ? false
        // Added 2026-11-19: looks saved before it existed keep the density-domain final blur.
        : key is "grain-synthesis-transmission-blur" ? false
        : baseline;
}
