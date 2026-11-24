#pragma once

#include <cstdint>

#if __has_include("SpektraGeneratedProfileCounts.h")
#  include "SpektraGeneratedProfileCounts.h"
#endif

namespace spektrafilm {

constexpr uint32_t kSpektraColorSpaceCount = 26u;
constexpr uint32_t kSpektraColorTransferLutSize = 4096u;
constexpr uint32_t kSpektraOutputGamutCompressionStride = 18u;
constexpr uint32_t kSpektraOutputGamutCompressionElementCount =
  kSpektraColorSpaceCount * kSpektraOutputGamutCompressionStride;
// Spektrafilm C1 film model 4: CAM16-UCS output gamut data per colour space (generate_profile_curves.py
// write_output_gamut_cam16_data): a 32-float header and the 64 x 720 Cp_max(Jp, hp) table.
constexpr uint32_t kSpektraOutputGamutCam16Stride = 32u + 64u * 720u;
constexpr uint32_t kSpektraOutputGamutCam16ElementCount = kSpektraColorSpaceCount * kSpektraOutputGamutCam16Stride;

#ifndef SPEKTRA_GENERATED_PROFILE_COUNTS
constexpr uint32_t kSpektraFilmCount = 20u;
constexpr uint32_t kSpektraPaperCount = 7u;
constexpr int32_t kSpektraDefaultFilmIndex = 2;
constexpr int32_t kSpektraDefaultPaperIndex = 3;
constexpr bool kSpektraAcademyPrinterDensityEnabled = true;
#endif

struct ProfileCurveSet {
  const char *stock = nullptr;
  const char *name = nullptr;
  const char *type = nullptr;
  const char *referenceIlluminant = nullptr;
  uint32_t wavelengthCount = 0;
  uint32_t exposureCount = 0;
  const float *wavelengths = nullptr;
  const float *logSensitivity = nullptr;
  const float *bandpassHanatos2025 = nullptr;
  const float *hanatos2026WindowParams = nullptr;
  const float *referenceIlluminantSpectrum = nullptr;
  const float *inputToReferenceXyz = nullptr;
  const float *inputToSrgb = nullptr;
  const float *mallettBasisIlluminant = nullptr;
  float mallettRawMidgrayGreen = 1.0f;
  const float *logExposure = nullptr;
  const float *densityCurves = nullptr;
  const float *channelDensity = nullptr;
  const float *baseDensity = nullptr;
  const float *densityCurveMinimum = nullptr;
  const float *densityCurveLayers = nullptr;
  const float *densityCurveLayerMaxima = nullptr;
  const float *halationStrength = nullptr;
  const float *halationFirstSigmaUm = nullptr;
  const float *dirGammaSameLayerRgb = nullptr;
  const float *dirGammaRToGb = nullptr;
  const float *dirGammaGToRb = nullptr;
  const float *dirGammaBToRg = nullptr;
  const float *scanIlluminant = nullptr;
  const float *scanToOutputRgb = nullptr;
  // Spektrafilm C1 film model 2: input RGB to reference-illuminant XYZ with CAT16 (model 1 uses inputToReferenceXyz, CAT02).
  const float *inputToReferenceXyzCat16 = nullptr;
};

struct HanatosSpectraLutInfo {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t wavelengthCount = 0;
  uint32_t elementCount = 0;
};

// Fitted CDF layers are kept separately from legacy sampled profiles so adding
// print morph controls does not replace existing film/paper characteristics.
struct PrintCurveModel {
  const char *stock;
  bool positive;
  double centers[3][3];
  double amplitudes[3][3];
  double sigmas[3][3];
};

// dataSet: 1 = the tables of film models 1 and 2, 3 = film model 3 (profiles of the Python reference, 2026-11-23).
const ProfileCurveSet *filmProfileCurves(int32_t index, int32_t dataSet = 1);
const ProfileCurveSet *paperProfileCurves(int32_t index, int32_t dataSet = 1);
bool profileDataSetAvailable(int32_t dataSet);
const HanatosSpectraLutInfo &hanatosSpectraLutInfo();
const float *inputMeterXyzMatrices();
const uint32_t *colorTransferKinds();
const float *colorTransferParams();
const char *colorSpaceLabel(int32_t index);
const float *colorDecodeLuts();
const float *colorEncodeLuts();
const float *standardObserverCmfs();
const float *thKg3Illuminant();
const float *customEnlargerFilters();
const float *neutralPrintFilters(int32_t dataSet = 1);
const float *academyPrinterDensityResponsivities();
const float *academyPrinterDensityNeutralOffsets();
const float *academyPrinterDensityData();
const float *academyPrinterDensityInfluxSpectrum();
float colorDecodeLutMin();
float colorDecodeLutMax();
float colorEncodeLutMin();
float colorEncodeLutMax();

} // namespace spektrafilm
