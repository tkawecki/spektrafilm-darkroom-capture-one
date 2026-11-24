#include "SpektraProfileCurves.h"
#include "SpektraRenderer.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <emmintrin.h>
#include <fcntl.h>
#include <io.h>
#include <iostream>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <vector>
#include <windows.h>

namespace {

using namespace spektrafilm;

struct NumericOption {
    std::string_view name;
    float RenderParams::*member;
    float min;
    float max;
};

constexpr NumericOption kNumericOptions[] = {
    {"film-exposure", &RenderParams::filmExposureEv, -8.0f, 8.0f},
    {"print-exposure", &RenderParams::printExposureEv, -5.0f, 5.0f},
    {"film-gamma", &RenderParams::filmGamma, 0.1f, 2.0f},
    {"print-gamma", &RenderParams::printGamma, 0.1f, 2.0f},
    {"print-exhaustion", &RenderParams::printDeveloperExhaustion, 0.0f, 1.0f},
    {"film-push-pull", &RenderParams::filmPushPullStops, -2.0f, 2.0f},
    {"print-push-pull", &RenderParams::printPushPullStops, -2.0f, 2.0f},
    {"negative-bleach-bypass", &RenderParams::negativeBleachBypassAmount, 0.0f, 1.0f},
    {"print-bleach-bypass", &RenderParams::printBleachBypassAmount, 0.0f, 1.0f},
    {"negative-leuco-cyan", &RenderParams::negativeLeucoCyanCoupling, 0.0f, 2.0f},
    {"print-shadow-shape", &RenderParams::printShadowShape, -1.0f, 1.0f},
    {"print-highlight-shape", &RenderParams::printHighlightShape, -1.0f, 1.0f},
    {"film-base-scale", &RenderParams::filmBaseScale, 0.0f, 5.0f},
    {"film-base-tilt", &RenderParams::filmBaseTilt, -2.0f, 2.0f},
    {"film-base-cyan", &RenderParams::filmBaseCyan, 0.0f, 5.0f},
    {"film-base-magenta", &RenderParams::filmBaseMagenta, 0.0f, 5.0f},
    {"film-base-yellow", &RenderParams::filmBaseYellow, 0.0f, 5.0f},
    {"print-base-scale", &RenderParams::printBaseScale, 0.0f, 5.0f},
    {"print-base-cyan", &RenderParams::printBaseCyan, 0.0f, 5.0f},
    {"print-base-magenta", &RenderParams::printBaseMagenta, 0.0f, 5.0f},
    {"print-base-yellow", &RenderParams::printBaseYellow, 0.0f, 5.0f},
    {"film-age-years", &RenderParams::filmAgeYears, 0.0f, 100.0f},
    {"film-age-fog-r", &RenderParams::filmAgeFogR, 0.0f, 1.0f},
    {"film-age-fog-g", &RenderParams::filmAgeFogG, 0.0f, 1.0f},
    {"film-age-fog-b", &RenderParams::filmAgeFogB, 0.0f, 1.0f},
    {"film-age-gamma", &RenderParams::filmAgeGamma, 0.05f, 2.0f},
    {"film-age-gamma-r", &RenderParams::filmAgeGammaR, 0.05f, 2.0f},
    {"film-age-gamma-g", &RenderParams::filmAgeGammaG, 0.05f, 2.0f},
    {"film-age-gamma-b", &RenderParams::filmAgeGammaB, 0.05f, 2.0f},
    {"film-age-density", &RenderParams::filmAgeDensityScale, 0.05f, 2.0f},
    {"film-age-grain", &RenderParams::filmAgeGrainGain, 0.05f, 8.0f},
    {"film-age-speed-ev", &RenderParams::filmAgeSpeedLossEv, -3.0f, 6.0f},
    {"cyan-filter", &RenderParams::filterC, 0.0f, 120.0f},
    {"magenta-filter", &RenderParams::filterMShift, -60.0f, 60.0f},
    {"yellow-filter", &RenderParams::filterYShift, -60.0f, 60.0f},
    {"preflash", &RenderParams::preflashExposure, 0.0f, 1.0f},
    {"preflash-magenta", &RenderParams::preflashMFilterShift, -60.0f, 60.0f},
    {"preflash-yellow", &RenderParams::preflashYFilterShift, -60.0f, 60.0f},
    {"uv-cutoff", &RenderParams::cameraUvCutNm, 380.0f, 450.0f},
    {"ir-cutoff", &RenderParams::cameraIrCutNm, 600.0f, 780.0f},
    {"enlarger-scale", &RenderParams::enlargerScale, 1.0f, 32.0f},
    {"enlarger-x", &RenderParams::enlargerOffsetXPercent, -100.0f, 100.0f},
    {"enlarger-y", &RenderParams::enlargerOffsetYPercent, -100.0f, 100.0f},
    {"dir-amount", &RenderParams::dirCouplersAmount, 0.0f, 2.0f},
    {"dir-diffusion", &RenderParams::dirCouplersDiffusionUm, 0.0f, 100.0f},
    {"dir-tail-size", &RenderParams::dirCouplersDiffusionTailUm, 0.0f, 1000.0f},
    {"dir-tail-weight", &RenderParams::dirCouplersDiffusionTailWeight, 0.0f, 1.0f},
    {"dir-same-layer", &RenderParams::dirCouplersInhibitionSameLayer, 0.0f, 2.0f},
    {"dir-interlayer", &RenderParams::dirCouplersInhibitionInterlayer, 0.0f, 2.0f},
    {"dir-gamma-r", &RenderParams::dirCouplersGammaSameLayerR, 0.0f, 1.0f},
    {"dir-gamma-g", &RenderParams::dirCouplersGammaSameLayerG, 0.0f, 1.0f},
    {"dir-gamma-b", &RenderParams::dirCouplersGammaSameLayerB, 0.0f, 1.0f},
    {"dir-r-to-g", &RenderParams::dirCouplersGammaRToG, 0.0f, 1.0f},
    {"dir-r-to-b", &RenderParams::dirCouplersGammaRToB, 0.0f, 1.0f},
    {"dir-g-to-r", &RenderParams::dirCouplersGammaGToR, 0.0f, 1.0f},
    {"dir-g-to-b", &RenderParams::dirCouplersGammaGToB, 0.0f, 1.0f},
    {"dir-b-to-r", &RenderParams::dirCouplersGammaBToR, 0.0f, 1.0f},
    {"dir-b-to-g", &RenderParams::dirCouplersGammaBToG, 0.0f, 1.0f},
    {"scatter-amount", &RenderParams::scatterAmount, 0.0f, 2.0f},
    {"scatter-scale", &RenderParams::scatterScale, 0.0f, 4.0f},
    {"halation-amount", &RenderParams::halationAmount, 0.0f, 4.0f},
    {"halation-scale", &RenderParams::halationScale, 0.0f, 4.0f},
    {"halation-boost", &RenderParams::halationBoostEv, 0.0f, 20.0f},
    {"halation-boost-range", &RenderParams::halationBoostRange, 0.0f, 1.0f},
    {"halation-protect", &RenderParams::halationProtectEv, 0.0f, 10.0f},
    {"halation-red", &RenderParams::halationStrengthR, 0.0f, 1.0f},
    {"halation-green", &RenderParams::halationStrengthG, 0.0f, 1.0f},
    {"halation-blue", &RenderParams::halationStrengthB, 0.0f, 1.0f},
    {"camera-diffusion-strength", &RenderParams::cameraDiffusionStrength, 0.0f, 2.0f},
    {"camera-diffusion-scale", &RenderParams::cameraDiffusionSpatialScale, 0.0f, 4.0f},
    {"camera-diffusion-warmth", &RenderParams::cameraDiffusionHaloWarmth, -1.5f, 1.5f},
    {"camera-diffusion-core-intensity", &RenderParams::cameraDiffusionCoreIntensity, 0.0f, 4.0f},
    {"camera-diffusion-core-size", &RenderParams::cameraDiffusionCoreSize, 0.1f, 4.0f},
    {"camera-diffusion-halo-intensity", &RenderParams::cameraDiffusionHaloIntensity, 0.0f, 4.0f},
    {"camera-diffusion-halo-size", &RenderParams::cameraDiffusionHaloSize, 0.1f, 4.0f},
    {"camera-diffusion-bloom-intensity", &RenderParams::cameraDiffusionBloomIntensity, 0.0f, 4.0f},
    {"camera-diffusion-bloom-size", &RenderParams::cameraDiffusionBloomSize, 0.1f, 4.0f},
    {"print-diffusion-strength", &RenderParams::printDiffusionStrength, 0.0f, 2.0f},
    {"print-diffusion-scale", &RenderParams::printDiffusionSpatialScale, 0.0f, 4.0f},
    {"print-diffusion-warmth", &RenderParams::printDiffusionHaloWarmth, -1.5f, 1.5f},
    {"print-diffusion-core-intensity", &RenderParams::printDiffusionCoreIntensity, 0.0f, 4.0f},
    {"print-diffusion-core-size", &RenderParams::printDiffusionCoreSize, 0.1f, 4.0f},
    {"print-diffusion-halo-intensity", &RenderParams::printDiffusionHaloIntensity, 0.0f, 4.0f},
    {"print-diffusion-halo-size", &RenderParams::printDiffusionHaloSize, 0.1f, 4.0f},
    {"print-diffusion-bloom-intensity", &RenderParams::printDiffusionBloomIntensity, 0.0f, 4.0f},
    {"print-diffusion-bloom-size", &RenderParams::printDiffusionBloomSize, 0.1f, 4.0f},
    {"grain-amount", &RenderParams::grainAmount, 0.0f, 2.0f},
    {"grain-rms-r", &RenderParams::grainRmsR, 0.1f, 100.0f},
    {"grain-rms-g", &RenderParams::grainRmsG, 0.1f, 100.0f},
    {"grain-rms-b", &RenderParams::grainRmsB, 0.1f, 100.0f},
    {"grain-rms-strength", &RenderParams::grainRmsStrength, 0.001f, 4.0f},
    {"grain-saturation", &RenderParams::grainSaturation, 0.0f, 1.0f},
    {"grain-particle-area", &RenderParams::grainParticleAreaUm2, 0.01f, 5.0f},
    {"grain-final-blur", &RenderParams::grainFinalBlurUm, 0.0f, 25.0f},
    {"grain-dye-cloud-blur", &RenderParams::grainBlurDyeCloudsUm, 0.0f, 10.0f},
    {"grain-microstructure", &RenderParams::grainMicroStructureScale, 0.0f, 4.0f},
    {"grain-microstructure-sigma", &RenderParams::grainMicroStructureSigmaNm, 0.0f, 500.0f},
    {"grain-particle-scale-r", &RenderParams::grainParticleScaleR, 0.01f, 20.0f},
    {"grain-density-min-r", &RenderParams::grainDensityMinR, 0.0f, 1.0f},
    {"grain-uniformity-r", &RenderParams::grainUniformityR, 0.0f, 1.0f},
    {"grain-synthesis-radius-scale-r", &RenderParams::grainSynthesisRadiusScaleR, 0.01f, 20.0f},
    {"grain-particle-scale-g", &RenderParams::grainParticleScaleG, 0.01f, 20.0f},
    {"grain-density-min-g", &RenderParams::grainDensityMinG, 0.0f, 1.0f},
    {"grain-uniformity-g", &RenderParams::grainUniformityG, 0.0f, 1.0f},
    {"grain-synthesis-radius-scale-g", &RenderParams::grainSynthesisRadiusScaleG, 0.01f, 20.0f},
    {"grain-particle-scale-b", &RenderParams::grainParticleScaleB, 0.01f, 20.0f},
    {"grain-density-min-b", &RenderParams::grainDensityMinB, 0.0f, 1.0f},
    {"grain-uniformity-b", &RenderParams::grainUniformityB, 0.0f, 1.0f},
    {"grain-synthesis-radius-scale-b", &RenderParams::grainSynthesisRadiusScaleB, 0.01f, 20.0f},
    {"grain-layer-scale-0", &RenderParams::grainParticleScaleLayer0, 0.01f, 20.0f},
    {"grain-synthesis-layer-scale-0", &RenderParams::grainSynthesisLayerScale0, 0.01f, 20.0f},
    {"grain-layer-scale-1", &RenderParams::grainParticleScaleLayer1, 0.01f, 20.0f},
    {"grain-synthesis-layer-scale-1", &RenderParams::grainSynthesisLayerScale1, 0.01f, 20.0f},
    {"grain-layer-scale-2", &RenderParams::grainParticleScaleLayer2, 0.01f, 20.0f},
    {"grain-synthesis-layer-scale-2", &RenderParams::grainSynthesisLayerScale2, 0.01f, 20.0f},
    {"grain-synthesis-size", &RenderParams::grainSynthesisSize, 0.25f, 4.0f},
    {"grain-synthesis-amount", &RenderParams::grainSynthesisAmount, 0.0f, 3.0f},
    {"grain-synthesis-sharpness", &RenderParams::grainSynthesisSharpness, 0.25f, 4.0f},
    {"grain-synthesis-quality", &RenderParams::grainSynthesisQuality, 0.25f, 4.0f},
    {"grain-synthesis-radius", &RenderParams::grainSynthesisMeanRadiusUm, 0.05f, 10.0f},
    {"grain-synthesis-radius-sd", &RenderParams::grainSynthesisRadiusStdDevRatio, 0.0f, 1.0f},
    {"grain-synthesis-observation", &RenderParams::grainSynthesisObservationSigmaUm, 0.0f, 20.0f},
    {"grain-synthesis-cell", &RenderParams::grainSynthesisCellSizeRatio, 0.25f, 2.0f},
    {"grain-synthesis-radius-quantile", &RenderParams::grainSynthesisMaxRadiusQuantile, 0.95f, 0.9999f},
    {"grain-synthesis-epsilon", &RenderParams::grainSynthesisCoverageEpsilon, 0.000001f, 0.01f},
    {"scanner-white-level", &RenderParams::scannerWhiteLevel, 0.0f, 1.0f},
    {"scanner-black-level", &RenderParams::scannerBlackLevel, 0.0f, 1.0f},
    {"scanner-glare", &RenderParams::glarePercent, 0.0f, 0.2f},
    {"scanner-glare-roughness", &RenderParams::glareRoughness, 0.0f, 4.0f},
    {"scanner-glare-blur", &RenderParams::glareBlur, 0.0f, 32.0f},
    {"scanner-mtf", &RenderParams::scannerMtf50LpMm, 0.0f, 300.0f},
    {"scanner-unsharp-radius", &RenderParams::scannerUnsharpRadiusUm, 0.0f, 100.0f},
    {"scanner-unsharp-amount", &RenderParams::scannerUnsharpAmount, 0.0f, 4.0f},
};

struct BooleanOption {
    std::string_view name;
    bool RenderParams::*member;
};

constexpr BooleanOption kBooleanOptions[] = {
    {"grain-rms-stock", &RenderParams::grainRmsUseStock},
    {"color-adaptation", &RenderParams::colorAdaptation},
    {"color-input-compression", &RenderParams::colorAdaptationInputCompression},
    {"color-curve-smoothing", &RenderParams::colorAdaptationCurveSmoothing},
    {"color-output-lightness-compression", &RenderParams::colorAdaptationOutputLightnessCompression},
    {"color-output-chroma-compression", &RenderParams::colorAdaptationOutputChromaCompression},
    {"uv-enabled", &RenderParams::cameraUvFilterEnabled},
    {"ir-enabled", &RenderParams::cameraIrFilterEnabled},
    {"auto-exposure", &RenderParams::autoExposure},
    {"film-base-enabled", &RenderParams::filmBaseEnabled},
    {"print-base-enabled", &RenderParams::printBaseEnabled},
    {"film-aging-enabled", &RenderParams::filmAgingEnabled},
    {"grain-sublayers", &RenderParams::grainSublayersEnabled},
    {"grain-synthesis-layered", &RenderParams::grainSynthesisLayered},
    {"grain-mean-restore", &RenderParams::grainMeanRestore},
    {"grain-synthesis-aperture", &RenderParams::grainSynthesisAperture},
    {"grain-synthesis-noise-match", &RenderParams::grainSynthesisNoiseMatch},
    {"grain-synthesis-transmission-blur", &RenderParams::grainSynthesisTransmissionBlur},
    {"halation-protect-relative", &RenderParams::halationProtectRelative},
    {"scanner-enabled", &RenderParams::scannerEnabled},
    {"scanner-white", &RenderParams::scannerWhiteCorrection},
    {"scanner-black", &RenderParams::scannerBlackCorrection},
};

float number(const std::string &value, float low, float high) {
    std::size_t used = 0;
    const float result = std::stof(value, &used);
    if (used != value.size() || !std::isfinite(result) || result < low || result > high)
        throw std::invalid_argument("Value outside the permitted range: " + value);
    return result;
}

int integer(const std::string &value, int low, int high) {
    std::size_t used = 0;
    const int result = std::stoi(value, &used);
    if (used != value.size() || result < low || result > high)
        throw std::invalid_argument("Invalid integer: " + value);
    return result;
}

bool boolean(const std::string &value) {
    if (value == "true" || value == "1") return true;
    if (value == "false" || value == "0") return false;
    throw std::invalid_argument("Expected true or false: " + value);
}

template <typename Choice>
struct Option {
    std::string_view name;
    Choice value;
};

template <typename Choice, std::size_t N>
Choice select(const std::string &value, const Option<Choice> (&choices)[N]) {
    for (const auto &option : choices)
        if (value == option.name) return option.value;
    throw std::invalid_argument("Unknown option value: " + value);
}

constexpr Option<ColorSpace> kInputColorSpaces[] = {
    {"srgb", ColorSpace::Srgb}, {"linear_rec709", ColorSpace::LinearRec709},
    {"linear_rec2020", ColorSpace::LinearRec2020}, {"prophoto", ColorSpace::ProPhotoRgb},
    {"adobe_rgb", ColorSpace::AdobeRgb1998}, {"display_p3", ColorSpace::DisplayP3},
};

constexpr Option<ColorSpace> kOutputColorSpaces[] = {
    {"srgb", ColorSpace::Srgb}, {"rec709_gamma24", ColorSpace::Rec709Gamma24},
    {"linear_rec709", ColorSpace::LinearRec709},
};

constexpr Option<RgbToRawMethod> kUpsampling[] = {
    {"hanatos2026", RgbToRawMethod::Hanatos2026}, {"hanatos2025", RgbToRawMethod::Hanatos2025},
    {"mallett2019", RgbToRawMethod::Mallett2019},
};

constexpr Option<DebugTap> kDebugTaps[] = {
    {"off", DebugTap::None}, {"film-log", DebugTap::FilmLog}, {"film-density-pre-grain", DebugTap::FilmDensityPreGrain},
    {"film-density", DebugTap::FilmDensity}, {"print-log", DebugTap::PrintLog}, {"print-density", DebugTap::PrintDensity},
};

constexpr Option<DiffusionFilterFamily> kDiffusion[] = {
    {"glimmerglass", DiffusionFilterFamily::Glimmerglass}, {"black_pro_mist", DiffusionFilterFamily::BlackProMist},
    {"pro_mist", DiffusionFilterFamily::ProMist}, {"cine_bloom", DiffusionFilterFamily::CineBloom},
    {"cinebloom", DiffusionFilterFamily::CineBloom},
};

constexpr Option<GrainModel> kGrain[] = {
    {"preview", GrainModel::Preview}, {"production", GrainModel::Production},
    {"synthesis", GrainModel::GrainSynthesis},
    {"rms", GrainModel::RmsCalibrated},
};

constexpr Option<FilmFormat> kFormats[] = {
    {"8", FilmFormat::Standard8}, {"super8", FilmFormat::Super8},
    {"16", FilmFormat::Standard16}, {"super16", FilmFormat::Super16},
    {"35", FilmFormat::Standard35}, {"super35", FilmFormat::Super35},
    {"65", FilmFormat::Standard65}, {"70", FilmFormat::Imax70},
};

constexpr Option<ProcessMode> kProcesses[] = {
    {"print", ProcessMode::PrintSimulation}, {"scan", ProcessMode::ScanNegative},
    {"scan_inverted", ProcessMode::ScanNegative}, {"process_negative", ProcessMode::ProcessNegative},
};

int stockIndex(const std::string &id, bool film) {
    const auto count = film ? kSpektraFilmCount : kSpektraPaperCount;
    for (uint32_t index = 0; index < count; ++index) {
        const auto *profile = film ? filmProfileCurves(index) : paperProfileCurves(index);
        if (profile && profile->stock && id == profile->stock)
            return static_cast<int>(index);
    }
    throw std::invalid_argument("Unknown stock: " + id);
}

void setStockCalibration(RenderParams &p) {
    const auto *stock = filmProfileCurves(p.film, profileDataSet(p));
    if (!stock) return;
    if (stock->halationStrength) {
        p.halationStrengthR = stock->halationStrength[0];
        p.halationStrengthG = stock->halationStrength[1];
        p.halationStrengthB = stock->halationStrength[2];
    }
    if (stock->dirGammaSameLayerRgb) {
        p.dirCouplersGammaSameLayerR = stock->dirGammaSameLayerRgb[0];
        p.dirCouplersGammaSameLayerG = stock->dirGammaSameLayerRgb[1];
        p.dirCouplersGammaSameLayerB = stock->dirGammaSameLayerRgb[2];
    }
    if (stock->dirGammaRToGb && stock->dirGammaGToRb && stock->dirGammaBToRg) {
        p.dirCouplersGammaRToG = stock->dirGammaRToGb[0];
        p.dirCouplersGammaRToB = stock->dirGammaRToGb[1];
        p.dirCouplersGammaGToR = stock->dirGammaGToRb[0];
        p.dirCouplersGammaGToB = stock->dirGammaGToRb[1];
        p.dirCouplersGammaBToR = stock->dirGammaBToRg[0];
        p.dirCouplersGammaBToG = stock->dirGammaBToRg[1];
    }
}

void configure(RenderParams &p, std::string_view key, const std::string &value) {
    for (const auto &option : kNumericOptions) {
        if (key == option.name) {
            p.*option.member = number(value, option.min, option.max);
            return;
        }
    }
    for (const auto &option : kBooleanOptions) {
        if (key == option.name) {
            p.*option.member = boolean(value);
            return;
        }
    }
    if (key == "stock" || key == "paper") return;
    if (key == "input-color") p.inputColorSpace = select(value, kInputColorSpaces);
    else if (key == "output-color") p.outputColorSpace = select(value, kOutputColorSpaces);
    else if (key == "spectral-method") p.rgbToRawMethod = select(value, kUpsampling);
    else if (key == "film-model") p.filmModel = integer(value, 1, 4);
    else if (key == "debug-tap") p.debugTap = select(value, kDebugTaps);
    else if (key == "mode") {
        p.process = select(value, kProcesses);
        p.scanNegativeInvert = value == "scan_inverted";
    } else if (key == "film-format") p.filmFormat = select(value, kFormats);
    else if (key == "film-push-pull-mode") {
        constexpr Option<PushPullMode> modes[] = {
            {"standard", PushPullMode::Standard}, {"experimental", PushPullMode::Experimental},
        };
        p.filmPushPullMode = select(value, modes);
    } else if (key == "film-age-storage") {
        constexpr Option<FilmAgeStorage> storage[] = {
            {"frozen", FilmAgeStorage::Frozen}, {"cold", FilmAgeStorage::Cold},
            {"room", FilmAgeStorage::Room}, {"hot_humid", FilmAgeStorage::HotHumid},
            {"hot-humid", FilmAgeStorage::HotHumid},
        };
        p.filmAgeStorage = select(value, storage);
    } else if (key == "auto-exposure-meter") {
        constexpr Option<AutoExposureMethod> modes[] = {
            {"center_weighted", AutoExposureMethod::CenterWeighted}, {"median", AutoExposureMethod::Median},
            {"average", AutoExposureMethod::Average}, {"partial", AutoExposureMethod::Partial},
            {"matrix", AutoExposureMethod::Matrix}, {"multi_zone", AutoExposureMethod::MultiZone},
            {"highlight_weighted", AutoExposureMethod::HighlightWeighted},
        };
        p.autoExposureMethod = select(value, modes);
    } else if (key == "camera-diffusion-family") {
        p.cameraDiffusionEnabled = value != "off";
        if (p.cameraDiffusionEnabled) p.cameraDiffusionFamily = select(value, kDiffusion);
    } else if (key == "print-diffusion-family") {
        p.printDiffusionEnabled = value != "off";
        if (p.printDiffusionEnabled) p.printDiffusionFamily = select(value, kDiffusion);
    } else if (key == "grain-model") {
        p.grainEnabled = value != "off";
        if (p.grainEnabled) p.grainModel = select(value, kGrain);
    } else if (key == "grain-seed") p.grainSeed = integer(value, 0, 2147483647);
    else if (key == "grain-sublayer-count") p.grainSubLayerCount = integer(value, 1, 8);
    else if (key == "grain-synthesis-samples") p.grainSynthesisSamples = integer(value, 1, 2048);
    else if (key == "grain-synthesis-cell-cap") p.grainSynthesisMaxGrainsPerCell = integer(value, 1, 128);
    else throw std::invalid_argument("Unknown setting: --" + std::string(key));
}

RenderParams readParameters(const std::vector<std::string> &args) {
    RenderParams params;
    params.film = kSpektraDefaultFilmIndex;
    params.paper = kSpektraDefaultPaperIndex;
    params.inputColorSpace = ColorSpace::Srgb;
    params.outputColorSpace = ColorSpace::Srgb;
    params.grainFinalBlurUm = 7.17f;
    params.halationAmount = 0;
    params.scatterAmount = 0;

    if (args.size() % 2 != 0)
        throw std::invalid_argument("Each setting needs a value.");
    for (std::size_t arg = 0; arg < args.size(); arg += 2) {
        const std::string_view name(args[arg]);
        if (name == "--stock") params.film = stockIndex(args[arg + 1], true);
        if (name == "--paper") params.paper = stockIndex(args[arg + 1], false);
        // The stock defaults below come from the film model's own tables (film model 3 has its own data set).
        if (name == "--film-model") params.filmModel = integer(args[arg + 1], 1, 4);
    }
    setStockCalibration(params);
    for (std::size_t arg = 0; arg < args.size(); arg += 2) {
        const std::string_view name(args[arg]);
        if (name.rfind("--", 0) != 0)
            throw std::invalid_argument("Expected --setting name: " + std::string(name));
        configure(params, name.substr(2), args[arg + 1]);
    }
    params.halationEnabled = params.halationAmount > 0 || params.scatterAmount > 0;
    // OFX positions use a bottom-left origin. The preview buffer starts at the top.
    params.enlargerOffsetYPercent = -params.enlargerOffsetYPercent;
    if (std::abs(params.enlargerScale - 1.0f) <= 1.0e-6f) {
        params.enlargerOffsetXPercent = 0;
        params.enlargerOffsetYPercent = 0;
    }
    if (params.process == ProcessMode::ProcessNegative) {
        params.rgbToRawMethod = RgbToRawMethod::Hanatos2026;
        params.film = kSpektraDefaultFilmIndex;
        params.filmFormat = FilmFormat::Standard35;
        params.cameraUvFilterEnabled = false;
        params.cameraIrFilterEnabled = false;
        params.filmExposureEv = 0;
        params.autoExposure = false;
        params.filmPushPullMode = PushPullMode::Standard;
        params.filmPushPullStops = 0;
        params.negativeBleachBypassAmount = 0;
        params.negativeLeucoCyanCoupling = 1;
        params.filmGamma = 1;
        params.dirCouplersAmount = 0;
        params.grainEnabled = false;
        params.halationEnabled = false;
        params.cameraDiffusionEnabled = false;
        params.enlargerScale = 1;
        params.enlargerOffsetXPercent = 0;
        params.enlargerOffsetYPercent = 0;
    }
    return params;
}

// The public Vulkan renderer uses this transform for film coordinates but does
// not resample the source. Match upstream's spektrafilm_enlarger_resample Metal pass.
void resampleEnlarger(std::vector<float> &frame, int width, int height, const RenderParams &params) {
    if (std::abs(params.enlargerScale - 1.0f) <= 1.0e-6f)
        return;

    std::vector<float> enlarged(frame.size());
    const float invScale = 1 / params.enlargerScale;
    const float offsetX = params.enlargerOffsetXPercent * 0.01f * invScale;
    const float offsetY = params.enlargerOffsetYPercent * 0.01f * invScale;
    const auto sourcePixel = [&](int x, int y) {
        const auto row = std::clamp(y, 0, height - 1);
        const auto column = std::clamp(x, 0, width - 1);
        return &frame[(row * width + column) * 4];
    };

    for (int y = 0; y < height; ++y) {
        const float v = 0.5f + ((y + 0.5f) / height - 0.5f) * invScale + offsetY;
        for (int x = 0; x < width; ++x) {
            const float u = 0.5f + ((x + 0.5f) / width - 0.5f) * invScale + offsetX;
            auto *pixel = &enlarged[(y * width + x) * 4];
            if (u < 0 || u > 1 || v < 0 || v > 1) {
                pixel[3] = 1;
                continue;
            }
            const float sourceX = u * width - 0.5f;
            const float sourceY = v * height - 0.5f;
            const int x0 = static_cast<int>(std::floor(sourceX));
            const int y0 = static_cast<int>(std::floor(sourceY));
            const float tx = sourceX - x0;
            const float ty = sourceY - y0;
            const auto *p00 = sourcePixel(x0, y0);
            const auto *p10 = sourcePixel(x0 + 1, y0);
            const auto *p01 = sourcePixel(x0, y0 + 1);
            const auto *p11 = sourcePixel(x0 + 1, y0 + 1);
            for (int channel = 0; channel < 4; ++channel) {
                const float top = p00[channel] + (p10[channel] - p00[channel]) * tx;
                const float bottom = p01[channel] + (p11[channel] - p01[channel]) * tx;
                pixel[channel] = top + (bottom - top) * ty;
            }
        }
    }
    frame.swap(enlarged);
}

void enableBinaryPipes() {
    if (_setmode(_fileno(stdin), _O_BINARY) == -1 || _setmode(_fileno(stdout), _O_BINARY) == -1)
        throw std::runtime_error("Could not establish binary frame pipes.");
}

struct FrameSize {
    int width;
    int height;

    std::size_t componentCount() const {
        return static_cast<std::size_t>(width) * height * 4;
    }
};

struct InputFrame {
    FrameSize size;
    std::vector<float> pixels;
    uint64_t revision = 0;
};

struct FrameLimit {
    int maxSide;
    int maxPixels;
    const char *pixelError;
};

constexpr FrameLimit kPreviewLimit{2048, 1048576, "The in-app spatial preview accepts up to 1,048,576 pixels."};
constexpr FrameLimit kExportLimit{16384, 100000000, "The SpektraFilm export renderer accepts up to 100,000,000 pixels."};
constexpr FrameLimit kNativePreviewLimit{16384, 100000000, "The native Viewer accepts up to 100,000,000 pixels."};

constexpr int kNativeFullFramePixels = 32000000;

std::streamsize byteCount(std::size_t components) {
    static_assert(sizeof(float) == 4, "The frame protocol uses 32-bit floats.");
    return static_cast<std::streamsize>(components * sizeof(float));
}

FrameSize readFrameSize(const std::string &widthText, const std::string &heightText, const FrameLimit &limit) {
    const auto width = integer(widthText, 1, limit.maxSide);
    const auto height = integer(heightText, 1, limit.maxSide);
    if (width * height > limit.maxPixels)
        throw std::invalid_argument(limit.pixelError);
    return {width, height};
}

void checkFrameLength(std::streamsize expected) {
    if (std::cin.gcount() != expected)
        throw std::invalid_argument("The input frame has the wrong byte length.");
}

InputFrame readFrame(FrameSize size) {
    InputFrame frame{size, std::vector<float>(size.componentCount())};
    const auto expected = byteCount(frame.pixels.size());
    std::cin.read(reinterpret_cast<char *>(frame.pixels.data()), expected);
    checkFrameLength(expected);
    return frame;
}

void skipFrame(FrameSize size) {
    const auto expected = byteCount(size.componentCount());
    std::cin.ignore(expected);
    checkFrameLength(expected);
}

bool hasFiniteComponents(const float *pixels, std::size_t count) {
    // IEEE-754 exponent bits identify both infinities and every NaN payload.
    // This does not alter pixel values or the process floating-point mode.
    const auto exponent = _mm_set1_epi32(0x7f800000);
    std::size_t index = 0;
    for (; index + 4 <= count; index += 4) {
        const auto bits = _mm_castps_si128(_mm_loadu_ps(pixels + index));
        const auto nonfinite = _mm_cmpeq_epi32(_mm_and_si128(bits, exponent), exponent);
        if (_mm_movemask_ps(_mm_castsi128_ps(nonfinite))) return false;
    }
    for (; index < count; ++index)
        if (!std::isfinite(pixels[index])) return false;
    return true;
}

bool hasFinitePixels(const InputFrame &frame) {
    return hasFiniteComponents(frame.pixels.data(), frame.pixels.size());
}

std::unique_ptr<Renderer> startRenderer() {
    auto renderer = createNativeRenderer();
    if (!renderer || !renderer->isAvailable())
        throw std::runtime_error(renderer ? renderer->lastError() : "Vulkan renderer unavailable.");
    return renderer;
}

void renderFrame(Renderer &renderer, const InputFrame &input, const RenderParams &params, std::vector<float> &output) {
    const auto width = input.size.width;
    const auto height = input.size.height;
    const auto *pixels = input.pixels.data();
    auto revision = input.revision;
    std::vector<float> enlarged;
    if (std::abs(params.enlargerScale - 1.0f) > 1.0e-6f) {
        enlarged = input.pixels;
        resampleEnlarger(enlarged, width, height, params);
        pixels = enlarged.data();
        revision = 0;
    }

    output.resize(input.pixels.size());
    const int rowBytes = width * 4 * sizeof(float);
    const ImageView source{pixels, 0, 0, width, height, rowBytes, 4, sizeof(float), revision};
    const MutableImageView destination{output.data(), 0, 0, width, height, rowBytes, 4, sizeof(float)};
    const RenderWindow frame{0, 0, width, height};
    if (!renderer.render(source, destination, frame, params, 0.0))
        throw std::runtime_error(renderer.lastError());
    if (!hasFiniteComponents(output.data(), output.size()))
        throw std::runtime_error("Renderer produced a non-finite pixel.");
}

// Spektrafilm C1 Viewer window: renders only params.viewWindow of the resident frame. The renderer takes every
// full-frame step from the whole frame, so these pixels equal the same pixels of renderFrame's output.
void renderView(Renderer &renderer, const InputFrame &input, const RenderParams &params, std::vector<float> &output) {
    const auto width = input.size.width;
    const auto height = input.size.height;
    const auto &view = params.viewWindow;
    const auto *pixels = input.pixels.data();
    auto revision = input.revision;
    std::vector<float> enlarged;
    if (std::abs(params.enlargerScale - 1.0f) > 1.0e-6f) {
        enlarged = input.pixels;
        resampleEnlarger(enlarged, width, height, params);
        pixels = enlarged.data();
        revision = 0;
    }
    output.resize(static_cast<std::size_t>(view.width) * static_cast<std::size_t>(view.height) * 4u);
    const ImageView source{pixels, 0, 0, width, height, width * 4 * static_cast<int>(sizeof(float)), 4, sizeof(float), revision};
    const MutableImageView destination{output.data(), view.x, view.y, view.width, view.height,
                                       view.width * 4 * static_cast<int>(sizeof(float)), 4, sizeof(float)};
    if (!renderer.render(source, destination, RenderWindow{0, 0, width, height}, params, 0.0))
        throw std::runtime_error(renderer.lastError());
    if (!hasFiniteComponents(output.data(), output.size()))
        throw std::runtime_error("Renderer produced a non-finite pixel.");
}

void checkGrainSynthesisWork(const RenderParams &params);

void process(int argc, char **argv) {
    if (argc < 3 || argc % 2 == 0)
        throw std::invalid_argument("Usage: SpektraStillRenderer WIDTH HEIGHT [--setting VALUE ...], --stream, --export-stream, or --native-preview-stream");
    const auto params = readParameters(std::vector<std::string>(argv + 3, argv + argc));
    checkGrainSynthesisWork(params);
    enableBinaryPipes();
    const auto input = readFrame(readFrameSize(argv[1], argv[2], kPreviewLimit));
    if (!hasFinitePixels(input)) throw std::invalid_argument("Invalid input pixel.");
    const auto renderer = startRenderer();
    std::vector<float> output;
    renderFrame(*renderer, input, params, output);
    std::cout.write(reinterpret_cast<const char *>(output.data()), byteCount(output.size()));
    if (!std::cout)
        throw std::runtime_error("Could not send the rendered frame to Capture One.");
}

bool readCommand(std::string &line) {
    constexpr std::size_t limit = 16384;
    line.clear();
    while (true) {
        const auto ch = std::cin.rdbuf()->sbumpc();
        if (ch == std::char_traits<char>::eof()) {
            if (line.empty()) return false;
            throw std::invalid_argument("The renderer request ended before its line break.");
        }
        if (ch == '\n') return true;
        if (line.size() == limit)
            throw std::invalid_argument("The renderer request exceeds 16 KB.");
        if ((ch < ' ' || ch > '~') && ch != '\t' && ch != '\r')
            throw std::invalid_argument("The renderer command must contain ASCII text.");
        line.push_back(static_cast<char>(ch));
    }
}

void writeReplyHeader(uint32_t status, uint32_t length) {
    std::array<char, 8> header{};
    for (std::size_t byte = 0; byte < 4; ++byte) {
        header[byte] = static_cast<char>((status >> (byte * 8)) & 0xff);
        header[byte + 4] = static_cast<char>((length >> (byte * 8)) & 0xff);
    }
    std::cout.write(header.data(), header.size());
}

void flushReply() {
    std::cout.flush();
    if (!std::cout)
        throw std::runtime_error("Could not send the rendered frame to Capture One.");
}

void writeReply(uint32_t status, const char *data, uint32_t length) {
    writeReplyHeader(status, length);
    if (length != 0) std::cout.write(data, length);
    flushReply();
}

__m128i displayPixel(const float *rendered, const float *source) {
    const auto zero = _mm_setzero_ps();
    const auto one = _mm_set1_ps(1.0f);
    const auto rgb = _mm_min_ps(one, _mm_max_ps(zero, _mm_loadu_ps(rendered)));
    const auto scale = _mm_set1_pd(255.0);
    // Match the managed path's double RGB multiplication and round-to-even conversion.
    const auto rg = _mm_cvtpd_epi32(_mm_mul_pd(_mm_cvtps_pd(rgb), scale));
    const auto ba = _mm_cvtpd_epi32(_mm_mul_pd(_mm_cvtps_pd(_mm_movehl_ps(rgb, rgb)), scale));
    const auto bgra = _mm_shuffle_epi32(_mm_unpacklo_epi64(rg, ba), _MM_SHUFFLE(3, 0, 1, 2));
    // Display alpha comes from the original source and is multiplied in float precision.
    const auto sourcePixel = _mm_loadu_ps(source);
    const auto a = _mm_shuffle_ps(sourcePixel, sourcePixel, _MM_SHUFFLE(3, 3, 3, 3));
    const auto alpha = _mm_cvtps_epi32(_mm_mul_ps(_mm_min_ps(one, _mm_max_ps(zero, a)), _mm_set1_ps(255.0f)));
    const auto alphaMask = _mm_set_epi32(-1, 0, 0, 0);
    return _mm_or_si128(_mm_andnot_si128(alphaMask, bgra), _mm_and_si128(alphaMask, alpha));
}

void packDisplayPixels(char *destination, const float *rendered, const float *source, std::size_t count) {
    const auto vectorizedCount = count - count % 16;
    for (std::size_t offset = 0; offset < vectorizedCount; offset += 16) {
        const auto *rgb = rendered + offset;
        const auto *alpha = source + offset;
        const auto first = _mm_packs_epi32(displayPixel(rgb, alpha), displayPixel(rgb + 4, alpha + 4));
        const auto second = _mm_packs_epi32(displayPixel(rgb + 8, alpha + 8), displayPixel(rgb + 12, alpha + 12));
        _mm_storeu_si128(reinterpret_cast<__m128i *>(destination + offset), _mm_packus_epi16(first, second));
    }
    for (std::size_t offset = vectorizedCount; offset < count; offset += 4) {
        const auto words = _mm_packs_epi32(displayPixel(rendered + offset, source + offset), _mm_setzero_si128());
        const auto pixel = _mm_cvtsi128_si32(_mm_packus_epi16(words, _mm_setzero_si128()));
        std::memcpy(destination + offset, &pixel, sizeof(pixel));
    }
}

// Look strength below 1 for packed Viewer pixels. A double-precision port of the panel's
// SrgbTransform.GetSrgb/Blend (SpatialPreview.cs) for the identity-finishing, sRGB-transport
// case: the same 8192-step transfer tables built with the same CRT pow, the same operation
// order and round-half-to-even, so the bytes equal the managed conversion. It lets the panel
// skip downloading the float frame and blending it on the CPU.
struct DisplayBlend {
    static constexpr int kSteps = 8192;
    using Table = std::vector<double>;
    static Table build(double (*f)(double)) {
        Table t(kSteps + 1);
        for (int i = 0; i <= kSteps; ++i) t[i] = f(i / static_cast<double>(kSteps));
        return t;
    }
    static double decodeSrgb(double v) { return v <= 0.04045 ? v / 12.92 : std::pow((v + 0.055) / 1.055, 2.4); }
    static double encodeSrgb(double v) { return v <= 0.0031308 ? 12.92 * v : 1.055 * std::pow(v, 1 / 2.4) - 0.055; }
    static double decodeAdobe(double v) { return std::pow(v, 563.0 / 256); }
    static double decodeProPhoto(double v) { return v <= 1.0 / 32 ? v / 16 : std::pow(v, 1.8); }
    static const Table &srgbDecode() { static const Table t = build(decodeSrgb); return t; }
    static const Table &srgbEncode() { static const Table t = build(encodeSrgb); return t; }
    static const Table &adobeDecode() { static const Table t = build(decodeAdobe); return t; }
    static const Table &proPhotoDecode() { static const Table t = build(decodeProPhoto); return t; }
    static double sample(const Table &t, double value) {
        const double coordinate = std::clamp(value, 0.0, 1.0) * kSteps;
        const int lower = std::min(static_cast<int>(coordinate), kSteps - 1);
        return t[lower] + (coordinate - lower) * (t[lower + 1] - t[lower]);
    }

    double strength;
    ColorSpace space;
    const Table *input = nullptr;

    DisplayBlend(double s, ColorSpace inputSpace) : strength(s), space(inputSpace) {
        if (space == ColorSpace::AdobeRgb1998) input = &adobeDecode();
        else if (space == ColorSpace::ProPhotoRgb) input = &proPhotoDecode();
        else if (space != ColorSpace::Srgb && space != ColorSpace::LinearRec2020)
            throw std::invalid_argument("Packed Viewer look strength supports sRGB, Adobe RGB, ProPhoto RGB and linear Rec.2020 input.");
        srgbDecode(); srgbEncode();
    }
    double decodeInput(float v) const { return input ? sample(*input, v) : v; }
    double blend(double baseline, float processed) const {
        const double display = processed;
        const double film = display >= 0 && display <= 1 ? sample(srgbDecode(), display)
            : std::copysign(decodeSrgb(std::abs(display)), display);
        return sample(srgbEncode(), baseline + strength * (film - baseline));
    }
    void pixel(char *out, const float *r, const float *s) const {
        double blue, green, red;
        if (space == ColorSpace::Srgb) {
            blue = sample(srgbDecode(), s[2]); green = sample(srgbDecode(), s[1]); red = sample(srgbDecode(), s[0]);
        } else {
            const double R = decodeInput(s[0]), G = decodeInput(s[1]), B = decodeInput(s[2]);
            if (space == ColorSpace::AdobeRgb1998) {
                blue = -0.04294 * G + 1.04294 * B; green = G; red = 1.39828 * R - 0.39828 * G;
            } else if (space == ColorSpace::ProPhotoRgb) {
                blue = -0.008555533 * R - 0.153286575 * G + 1.161842108 * B;
                green = -0.228798056 * R + 1.231730282 * G - 0.002932226 * B;
                red = 2.034218153 * R - 0.727536473 * G - 0.306681680 * B;
            } else {
                blue = -0.018150763 * R - 0.100578898 * G + 1.118729661 * B;
                green = -0.124550475 * R + 1.132899897 * G - 0.008349423 * B;
                red = 1.660491002 * R - 0.587641139 * G - 0.072849863 * B;
            }
        }
        out[0] = static_cast<char>(static_cast<unsigned char>(std::nearbyint(blend(blue, r[2]) * 255)));
        out[1] = static_cast<char>(static_cast<unsigned char>(std::nearbyint(blend(green, r[1]) * 255)));
        out[2] = static_cast<char>(static_cast<unsigned char>(std::nearbyint(blend(red, r[0]) * 255)));
        const float a = std::nearbyint(255.0f * std::clamp(s[3], 0.0f, 1.0f));
        out[3] = static_cast<char>(static_cast<unsigned char>(a));
    }
    void pack(char *destination, const float *rendered, const float *source, std::size_t count) const {
        const std::size_t pixels = count / 4;
        const unsigned workers = pixels < (1u << 20) ? 1u
            : std::clamp(std::thread::hardware_concurrency() / 4u, 1u, 8u);
        auto run = [&](std::size_t first, std::size_t last) {
            for (std::size_t p = first; p < last; ++p)
                pixel(destination + p * 4, rendered + p * 4, source + p * 4);
        };
        if (workers == 1) { run(0, pixels); return; }
        std::vector<std::thread> threads;
        for (unsigned w = 0; w < workers; ++w)
            threads.emplace_back(run, pixels * w / workers, pixels * (w + 1) / workers);
        for (auto &thread : threads) thread.join();
    }
};

void packDisplayPixels(char *destination, const float *rendered, const float *source, std::size_t count,
                       const DisplayBlend *blend) {
    if (blend) blend->pack(destination, rendered, source, count);
    else packDisplayPixels(destination, rendered, source, count);
}

void writePackedPreview(const InputFrame &input, const std::vector<float> &rendered, const DisplayBlend *blend) {
    alignas(16) std::array<char, 64 * 1024> buffer;
    writeReplyHeader(0, static_cast<uint32_t>(rendered.size()));
    for (std::size_t start = 0; start < rendered.size(); start += buffer.size()) {
        const auto count = std::min(buffer.size(), rendered.size() - start);
        packDisplayPixels(buffer.data(), rendered.data() + start, input.pixels.data() + start, count, blend);
        if (!std::cout.write(buffer.data(), count))
            throw std::runtime_error("Could not send the rendered frame to Capture One.");
    }
    flushReply();
}

void writeError(std::string_view message) {
    if (message.size() > 4096) message = message.substr(0, 4096);
    writeReply(1, message.data(), static_cast<uint32_t>(message.size()));
}

// Panel <-> renderer handshake, adapted from nothing-complex/spektrafilm-for-capture-one 285f0aa
// (GPL-3.0). The panel sends "HELLO <version>" before its first frame; a renderer from another build
// answers with an error instead of misreading later commands (for example --display-strength).
// Bump the version whenever a panel would send something an older renderer cannot handle.
// Clients that never send HELLO (test scripts, one-shot mode) keep working.
constexpr int kStreamProtocol = 3;  // 3: VIEW/VIEW8 Viewer windows (2026-11-23)

void answerHello(std::istringstream &command, const FrameLimit &limit) {
    std::string version, extra;
    if (!(command >> version) || command >> extra) {
        writeError("HELLO requires one protocol version.");
        return;
    }
    if (version != std::to_string(kStreamProtocol)) {
        writeError("This Spektrafilm renderer uses stream protocol " + std::to_string(kStreamProtocol) +
                   ", the panel uses " + version + ". Install the panel and renderer from the same build.");
        return;
    }
    std::ostringstream json;
    json << "{\"protocol\":" << kStreamProtocol << ",\"maxSide\":" << limit.maxSide
         << ",\"maxPixels\":" << limit.maxPixels << '}';
    const auto text = json.str();
    writeReply(0, text.data(), static_cast<uint32_t>(text.size()));
}

// Native preview only. The client owns the mapping; success headers are sent
// after all pixels are ready, while errors always remain on the control pipe.
// Packed Viewer pixels of a window: the source alpha comes from the same rows of the resident full frame.
void packViewPixels(char *destination, const std::vector<float> &rendered, const InputFrame &input,
                    const ViewWindow &view, const DisplayBlend *blend) {
    const std::size_t row = static_cast<std::size_t>(view.width) * 4u;
    for (int y = 0; y < view.height; ++y) {
        const std::size_t sourceOffset =
            (static_cast<std::size_t>(view.y + y) * static_cast<std::size_t>(input.size.width) + static_cast<std::size_t>(view.x)) * 4u;
        packDisplayPixels(destination + y * row, rendered.data() + y * row, input.pixels.data() + sourceOffset, row, blend);
    }
}

void writePackedView(const InputFrame &input, const std::vector<float> &rendered, const ViewWindow &view,
                     const DisplayBlend *blend) {
    std::vector<char> packed(rendered.size());
    packViewPixels(packed.data(), rendered, input, view, blend);
    writeReplyHeader(0, static_cast<uint32_t>(packed.size()));
    if (!std::cout.write(packed.data(), static_cast<std::streamsize>(packed.size())))
        throw std::runtime_error("Could not send the rendered frame to Capture One.");
    flushReply();
}

struct SharedPreviewBuffer {
    HANDLE handle = nullptr;
    void *view = nullptr;
    std::size_t capacity = 0;

    ~SharedPreviewBuffer() { reset(); }
    void reset() {
        if (view) UnmapViewOfFile(view);
        if (handle) CloseHandle(handle);
        view = nullptr;
        handle = nullptr;
        capacity = 0;
    }
    void open(const std::string &name, std::size_t size, bool input = false) {
        const std::string_view prefix = input ? "Local\\SpektrafilmSource-" : "Local\\SpektrafilmPreview-";
        if (name.size() != prefix.size() + 32 || name.compare(0, prefix.size(), prefix) != 0 ||
            !std::all_of(name.begin() + prefix.size(), name.end(), [](char c) {
                return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            })) throw std::invalid_argument("Invalid shared preview mapping name.");
        const auto access = input ? FILE_MAP_READ : FILE_MAP_WRITE;
        auto nextHandle = OpenFileMappingA(access, FALSE, name.c_str());
        if (!nextHandle) throw std::runtime_error("Could not open the shared preview mapping.");
        auto nextView = MapViewOfFile(nextHandle, access, 0, 0, size);
        if (!nextView) {
            CloseHandle(nextHandle);
            throw std::runtime_error("Could not map the shared preview output.");
        }
        reset();
        handle = nextHandle;
        view = nextView;
        capacity = size;
    }
    InputFrame receive(FrameSize size) const {
        const auto bytes = static_cast<std::size_t>(byteCount(size.componentCount()));
        if (!view || bytes > capacity)
            throw std::invalid_argument("Shared source mapping is missing or too small for this frame.");
        InputFrame frame{size, std::vector<float>(size.componentCount())};
        // Own the validated source so later client writes cannot alter REUSE.
        std::memcpy(frame.pixels.data(), view, bytes);
        return frame;
    }
    void sendView(const InputFrame &input, const std::vector<float> &output, bool packed, const ViewWindow &window,
                  const DisplayBlend *blend) {
        const auto size = packed ? output.size() : output.size() * sizeof(float);
        if (size > capacity) {
            writeError("Shared preview mapping is too small for this frame.");
            return;
        }
        if (packed) packViewPixels(static_cast<char *>(view), output, input, window, blend);
        else std::memcpy(view, output.data(), size);
        writeReplyHeader(0, static_cast<uint32_t>(size));
        flushReply();
    }
    void send(const InputFrame &input, const std::vector<float> &output, bool packed, const DisplayBlend *blend) {
        const auto size = packed ? output.size() : output.size() * sizeof(float);
        if (size > capacity) {
            writeError("Shared preview mapping is too small for this frame.");
            return;
        }
        if (packed) packDisplayPixels(static_cast<char *>(view), output.data(), input.pixels.data(), output.size(), blend);
        else std::memcpy(view, output.data(), size);
        writeReplyHeader(0, static_cast<uint32_t>(size));
        flushReply();
    }
};

// Inverse standard normal CDF (Acklam's rational approximation, |error| < 1.2e-9).
double inverseNormal(double p) {
    static const double a[] = {-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                               1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00};
    static const double b[] = {-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                               6.680131188771972e+01, -1.328068155288572e+01};
    static const double c[] = {-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                               -2.549671010114370e+00, 4.374664141464968e+00, 2.938163982698783e+00};
    static const double d[] = {7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                               3.754408661907416e+00};
    p = std::clamp(p, 1.0e-12, 1.0 - 1.0e-12);
    if (p < 0.02425) {
        const double q = std::sqrt(-2.0 * std::log(p));
        return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
               ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
    }
    if (p > 1.0 - 0.02425) return -inverseNormal(1.0 - p);
    const double q = p - 0.5, r = q * q;
    return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
           (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0);
}

// Grain synthesis tests every grain cell within the largest grain radius, for every sample, in
// one GPU invocation per pixel (SpektraGrain.comp grainSynthesisIndicator). Above a certain amount
// of work a single workgroup outlives the Windows GPU watchdog (TDR) and the driver resets the
// device, which can also disturb Capture One. Measured on an RTX 4090 with default settings:
// work 0.49M and 0.67M rendered, 0.98M and 1.10M reset the device. Refuse such settings.
constexpr double kGrainSynthesisWorkBudget = 700000.0;

double grainSynthesisWorkPerPixel(const RenderParams &params) {
    const double samples = std::clamp<double>(std::lround(params.grainSynthesisSamples *
        std::clamp(params.grainSynthesisQuality, 0.25f, 4.0f)), 1.0, 1024.0);
    const double mean = params.grainSynthesisMeanRadiusUm * std::clamp(params.grainSynthesisSize, 0.25f, 4.0f);
    const double cell = std::max(mean * std::max<double>(params.grainSynthesisCellSizeRatio, 0.05), 1.0e-4);
    const double ratio = std::max<double>(params.grainSynthesisRadiusStdDevRatio, 0.0);
    const double logSigma = ratio > 1.0e-6 ? std::sqrt(std::log(1.0 + ratio * ratio)) : 0.0;
    const double z = inverseNormal(params.grainSynthesisMaxRadiusQuantile);
    const double channels[] = {params.grainSynthesisRadiusScaleR, params.grainSynthesisRadiusScaleG, params.grainSynthesisRadiusScaleB};
    const double layers[] = {params.grainSynthesisLayerScale0, params.grainSynthesisLayerScale1, params.grainSynthesisLayerScale2};
    double cells = 0.0;
    for (int layer = 0; layer < (params.grainSynthesisLayered ? 3 : 1); ++layer)
        for (double channel : channels) {
            const double scaled = std::max(mean * std::max(channel, 1.0e-6) *
                (params.grainSynthesisLayered ? std::max(layers[layer], 1.0e-6) : 1.0), 1.0e-6);
            const double maxRadius = logSigma > 0.0
                ? std::max(scaled, std::exp(std::log(scaled) - 0.5 * logSigma * logSigma + logSigma * z)) : scaled;
            const double span = std::floor(2.0 * maxRadius / cell) + 1.0;
            cells = std::max(cells, span * span);
        }
    return samples * cells;
}

void checkGrainSynthesisWork(const RenderParams &params) {
    if (!params.grainEnabled || params.grainModel != GrainModel::GrainSynthesis) return;
    const double work = grainSynthesisWorkPerPixel(params);
    if (work > kGrainSynthesisWorkBudget) {
        std::ostringstream message;
        message << "These Grain synthesis settings would take too long per pixel for the GPU watchdog and could reset "
                   "the graphics driver (work " << std::lround(work / 1000.0) << "k, limit "
                << std::lround(kGrainSynthesisWorkBudget / 1000.0) << "k). Lower Samples or Quality, Radius variation, "
                   "Max radius quantile or the radius/layer scales, or raise Cell ratio. Nothing was rendered.";
        throw std::invalid_argument(message.str());
    }
}

void configureLargeFrame(RenderParams &params, FrameSize size, bool exporting) {
    checkGrainSynthesisWork(params);
    if (size.width * size.height <= kNativeFullFramePixels) return;
    if (params.grainEnabled && params.grainModel == GrainModel::GrainSynthesis)
        throw std::invalid_argument(exporting
            ? "Grain synthesis supports export canvases up to 32,000,000 pixels. Choose another grain model or a smaller export size; grain quality was not reduced."
            : "Native Viewer grain synthesis supports up to 32,000,000 pixels. Choose a smaller Viewer preview size or switch grain models.");
    // Use the OFX renderer's overlap and full-frame coordinates to bound the
    // optical scratch buffers while keeping the original sampling resolution.
    params.gpuRenderTiling = GpuRenderTilingMode::Tiled;
}

// Options that act only after the developed negative (print, enlarger filtration, scan and output). When a Viewer
// request changes nothing else, the renderer may reuse the negative it developed for the previous request. An option
// missing from this list only costs speed: any other change develops the negative again.
bool isPrintSideOption(const std::string &option) {
    static const std::array<std::string_view, 43> kPrintSide = {
        "--paper", "--print-exposure", "--print-gamma", "--print-push-pull", "--print-bleach-bypass",
        "--print-shadow-shape", "--print-highlight-shape", "--cyan-filter", "--magenta-filter", "--yellow-filter",
        "--preflash", "--preflash-magenta", "--preflash-yellow", "--print-exhaustion",
        "--print-diffusion-family", "--print-diffusion-strength", "--print-diffusion-scale", "--print-diffusion-warmth",
        "--print-diffusion-core-intensity", "--print-diffusion-core-size", "--print-diffusion-halo-intensity",
        "--print-diffusion-halo-size", "--print-diffusion-bloom-intensity", "--print-diffusion-bloom-size",
        "--print-base-enabled", "--print-base-scale", "--print-base-cyan", "--print-base-magenta", "--print-base-yellow",
        "--scanner-enabled", "--scanner-white-level", "--scanner-black-level", "--scanner-white", "--scanner-black",
        "--scanner-glare", "--scanner-glare-roughness", "--scanner-glare-blur", "--scanner-mtf",
        "--scanner-unsharp-radius", "--scanner-unsharp-amount", "--output-color",
        "--color-output-lightness-compression", "--color-output-chroma-compression",
    };
    return std::find(kPrintSide.begin(), kPrintSide.end(), option) != kPrintSide.end();
}

// The options that decide the developed negative: all except the print-side ones (option/value pairs).
std::vector<std::string> filmStageArguments(const std::vector<std::string> &args) {
    std::vector<std::string> film;
    for (std::size_t i = 0; i < args.size(); i += 2) {
        if (i + 1 < args.size() && isPrintSideOption(args[i])) continue;
        film.push_back(args[i]);
        if (i + 1 < args.size()) film.push_back(args[i + 1]);
    }
    return film;
}

void processStream(const FrameLimit &limit) {
    enableBinaryPipes();
    std::unique_ptr<Renderer> renderer;
    InputFrame input{};
    std::vector<float> output;
    std::optional<std::vector<std::string>> renderedArguments;
    // Film-side options of the last completed Viewer render; equal options of the next request let the renderer
    // reuse its developed negative.
    std::optional<std::vector<std::string>> renderedFilmArguments;
    // Last Viewer window (VIEW/VIEW8): its own output and key, so it never replaces the full frame's.
    std::vector<float> viewOutput;
    std::optional<std::vector<std::string>> renderedViewKey;
    const bool nativePreview = &limit == &kNativePreviewLimit;
    SharedPreviewBuffer sharedOutput, sharedInput;
    uint64_t nextRevision = 0;
    std::string line;
    try {
        while (readCommand(line)) {
            std::istringstream command(line);
            std::string verb;
            command >> verb;
            if (verb == "HELLO") {
                answerHello(command, limit);
                continue;
            }
            if ((verb == "MAP" || verb == "SOURCE") && nativePreview) {
                try {
                    std::string name, size, extra;
                    if (!(command >> name >> size) || command >> extra)
                        throw std::invalid_argument("MAP requires a name and byte capacity.");
                    auto &buffer = verb == "SOURCE" ? sharedInput : sharedOutput;
                    buffer.open(name, static_cast<std::size_t>(integer(size, 1, 1600000000)), verb == "SOURCE");
                    writeReply(0, nullptr, 0);
                } catch (const std::exception &error) { writeError(error.what()); }
                continue;
            }
            if ((verb == "INPUT" || verb == "MINPUT") && nativePreview) {
                // INPUT W H + pixels, or MINPUT W H from the shared source mapping: replaces the resident frame
                // without rendering it, so a Viewer window (VIEW8) can follow without a full render first.
                std::string width, height;
                command >> width >> height;
                // An invalid frame length makes locating the next header impossible (as for FRAME).
                const auto size = readFrameSize(width, height, limit);
                InputFrame replacement{};
                if (verb == "MINPUT") {
                    try { replacement = sharedInput.receive(size); }
                    catch (const std::exception &error) { writeError(error.what()); continue; }
                } else replacement = readFrame(size);
                if (!hasFinitePixels(replacement)) {
                    writeError("Invalid input pixel.");
                    continue;
                }
                replacement.revision = ++nextRevision;
                input = std::move(replacement);
                renderedArguments.reset();
                renderedFilmArguments.reset();
                renderedViewKey.reset();
                writeReply(0, nullptr, 0);
                continue;
            }
            if ((verb == "VIEW" || verb == "VIEW8") && nativePreview) {
                // VIEW x y w h --options...: a window of the resident frame (sent by FRAME/MFRAME before).
                try {
                    std::string xs, ys, ws, hs;
                    if (!(command >> xs >> ys >> ws >> hs))
                        throw std::invalid_argument("VIEW requires x, y, width and height.");
                    std::vector<std::string> viewArgs;
                    for (std::string option; command >> option;) viewArgs.push_back(std::move(option));
                    std::optional<double> viewStrength;
                    for (std::size_t i = 0; i + 1 < viewArgs.size(); i += 2)
                        if (viewArgs[i] == "--display-strength") {
                            try { viewStrength = std::stod(viewArgs[i + 1]); } catch (const std::exception &) { viewStrength = -1.0; }
                            viewArgs.erase(viewArgs.begin() + static_cast<std::ptrdiff_t>(i), viewArgs.begin() + static_cast<std::ptrdiff_t>(i) + 2);
                            break;
                        }
                    if (input.pixels.empty()) throw std::invalid_argument("Send a FRAME before requesting a VIEW.");
                    const bool packedView = verb == "VIEW8";
                    auto params = readParameters(viewArgs);
                    if (packedView && params.outputColorSpace != ColorSpace::Srgb)
                        throw std::invalid_argument("Packed Viewer pixels require sRGB output.");
                    if (viewStrength && (!packedView || !(*viewStrength > 0.0 && *viewStrength <= 1.0)))
                        throw std::invalid_argument("--display-strength needs packed Viewer pixels and a value in (0, 1].");
                    configureLargeFrame(params, input.size, false);
                    params.viewWindow = ViewWindow{true, integer(xs, 0, input.size.width - 1), integer(ys, 0, input.size.height - 1),
                                                   integer(ws, 1, input.size.width), integer(hs, 1, input.size.height)};
                    const auto &window = params.viewWindow;
                    if (window.x + window.width > input.size.width || window.y + window.height > input.size.height)
                        throw std::invalid_argument("The Viewer window lies outside the frame.");
                    auto key = viewArgs;
                    key.insert(key.end(), {xs, ys, ws, hs, std::to_string(input.revision)});
                    if (!renderedViewKey || key != *renderedViewKey) {
                        renderedViewKey.reset();
                        if (!renderer) renderer = startRenderer();
                        try { renderView(*renderer, input, params, viewOutput); }
                        catch (...) { renderer.reset(); throw; }
                        renderedViewKey = std::move(key);
                        // A window renders through the tiled path, which leaves no reusable developed negative.
                        renderedFilmArguments.reset();
                    }
                    std::optional<DisplayBlend> blend;
                    if (viewStrength && *viewStrength < 1.0) blend.emplace(*viewStrength, params.inputColorSpace);
                    if (sharedOutput.view) sharedOutput.sendView(input, viewOutput, packedView, window, blend ? &*blend : nullptr);
                    else if (packedView) writePackedView(input, viewOutput, window, blend ? &*blend : nullptr);
                    else writeReply(0, reinterpret_cast<const char *>(viewOutput.data()), static_cast<uint32_t>(byteCount(viewOutput.size())));
                } catch (const std::exception &error) { writeError(error.what()); }
                continue;
            }
            if (verb == "QUIT") {
                std::string extra;
                if (command >> extra) {
                    writeError("QUIT does not take arguments.");
                    continue;
                }
                writeReply(0, nullptr, 0);
                return;
            }

            const bool mappedInput = nativePreview && (verb == "MFRAME" || verb == "MFRAME8");
            const bool packedPreview = nativePreview && (verb == "FRAME8" || verb == "REUSE8" || verb == "MFRAME8");
            const bool replacesInput = verb == "FRAME" || (packedPreview && verb == "FRAME8") || mappedInput;
            FrameSize size{};
            if (replacesInput) {
                std::string width, height;
                command >> width >> height;
                // An invalid frame length makes locating the next header impossible.
                size = readFrameSize(width, height, limit);
            } else if (verb != "REUSE" && !packedPreview) {
                throw std::invalid_argument("Expected a FRAME, REUSE, or QUIT renderer command.");
            }

            std::vector<std::string> args;
            for (std::string option; command >> option;) args.push_back(std::move(option));
            // Viewer-only look strength for packed pixels. Not a film setting: it is removed before
            // the arguments are compared, so a strength change only re-packs the cached frame.
            std::optional<double> displayStrength;
            for (std::size_t i = 0; i + 1 < args.size(); i += 2)
                if (args[i] == "--display-strength") {
                    try { displayStrength = std::stod(args[i + 1]); } catch (const std::exception &) { displayStrength = -1.0; }
                    args.erase(args.begin() + static_cast<std::ptrdiff_t>(i), args.begin() + static_cast<std::ptrdiff_t>(i) + 2);
                    break;
                }
            RenderParams params;
            try {
                if (!replacesInput && input.pixels.empty())
                    throw std::invalid_argument("Send a FRAME before requesting REUSE.");
                params = readParameters(args);
                if (packedPreview && params.outputColorSpace != ColorSpace::Srgb)
                    throw std::invalid_argument("Packed Viewer pixels require sRGB output.");
                if (displayStrength && (!packedPreview || !(*displayStrength > 0.0 && *displayStrength <= 1.0)))
                    throw std::invalid_argument("--display-strength needs packed Viewer pixels and a value in (0, 1].");
                if (nativePreview || &limit == &kExportLimit)
                    configureLargeFrame(params, replacesInput ? size : input.size, &limit == &kExportLimit);
            } catch (const std::exception &error) {
                if (replacesInput && !mappedInput) skipFrame(size);
                writeError(error.what());
                continue;
            }

            InputFrame replacement{};
            if (replacesInput) {
                if (mappedInput) {
                    try { replacement = sharedInput.receive(size); }
                    catch (const std::exception &error) { writeError(error.what()); continue; }
                } else replacement = readFrame(size);
                if (!hasFinitePixels(replacement)) {
                    writeError("Invalid input pixel.");
                    continue;
                }
                if (nativePreview) replacement.revision = ++nextRevision;
            }
            if (replacesInput || !renderedArguments || args != *renderedArguments) {
                renderedArguments.reset();
                auto filmArguments = filmStageArguments(args);
                params.reuseFilmStage = nativePreview && !replacesInput && renderedFilmArguments &&
                    filmArguments == *renderedFilmArguments;
                renderedFilmArguments.reset();
                try {
                    if (!renderer) renderer = startRenderer();
                    renderFrame(*renderer, replacesInput ? replacement : input, params, output);
                } catch (const std::exception &error) {
                    renderer.reset();
                    writeError(error.what());
                    continue;
                }
                if (replacesInput) input = std::move(replacement);
                if (nativePreview) {
                    renderedArguments = std::move(args);
                    renderedFilmArguments = std::move(filmArguments);
                }
            }
            std::optional<DisplayBlend> blend;
            if (displayStrength && *displayStrength < 1.0) {
                try { blend.emplace(*displayStrength, params.inputColorSpace); }
                catch (const std::exception &error) { writeError(error.what()); continue; }
            }
            if (sharedOutput.view) {
                sharedOutput.send(input, output, packedPreview, blend ? &*blend : nullptr);
            } else if (packedPreview) {
                writePackedPreview(input, output, blend ? &*blend : nullptr);
            } else {
                writeReply(0, reinterpret_cast<const char *>(output.data()), static_cast<uint32_t>(byteCount(output.size())));
            }
        }
    } catch (const std::exception &error) {
        if (std::cout) writeError(error.what());
        throw;
    }
}

} // namespace

int main(int argc, char **argv) {
    try {
        const auto mode = argc == 2 ? std::string_view(argv[1]) : std::string_view{};
        if (mode == "--stream") processStream(kPreviewLimit);
        else if (mode == "--export-stream") processStream(kExportLimit);
        else if (mode == "--native-preview-stream") processStream(kNativePreviewLimit);
        else process(argc, argv);
        return 0;
    } catch (const std::exception &error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
