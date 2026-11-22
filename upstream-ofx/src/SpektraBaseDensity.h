#pragma once

// Spectral base controls adapted from The Dyeworks 2.2.3 model/develop.py
// (Andrea Volpato / Celian Le Bolloch, GPL-3.0). Preserve upstream notices.
#include "SpektraParameters.h"
#include "SpektraProfileCurves.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <vector>

namespace spektrafilm {

using FilmBaseKey = std::array<float, 5>; // scale, tilt, C, M, Y
using PrintBaseKey = std::array<float, 4>; // scale, C, M, Y
inline constexpr FilmBaseKey kNeutralFilmBase{1, 0, 1, 1, 1};
inline constexpr PrintBaseKey kNeutralPrintBase{1, 1, 1, 1};

inline FilmBaseKey filmBaseKey(const RenderParams &params) {
  return params.filmBaseEnabled
    ? FilmBaseKey{params.filmBaseScale, params.filmBaseTilt, params.filmBaseCyan, params.filmBaseMagenta, params.filmBaseYellow}
    : kNeutralFilmBase;
}

inline PrintBaseKey printBaseKey(const RenderParams &params) {
  return params.printBaseEnabled && params.process == ProcessMode::PrintSimulation
    ? PrintBaseKey{params.printBaseScale, params.printBaseCyan, params.printBaseMagenta, params.printBaseYellow}
    : kNeutralPrintBase;
}

inline std::vector<double> baseGaussianReflect(const std::vector<double> &values, double sigma) {
  const int radius = static_cast<int>(4.0 * sigma + 0.5);
  std::vector<double> weights(static_cast<size_t>(radius) * 2 + 1);
  double weightSum = 0.0;
  for (int k = -radius; k <= radius; ++k) {
    weights[k + radius] = std::exp(-0.5 * (k / sigma) * (k / sigma));
    weightSum += weights[k + radius];
  }
  for (auto &weight : weights) weight /= weightSum;
  std::vector<double> result(values.size());
  const int count = static_cast<int>(values.size());
  for (int i = 0; i < count; ++i) {
    for (int k = -radius; k <= radius; ++k) {
      int index = (i + k) % (2 * count);
      if (index < 0) index += 2 * count;
      if (index >= count) index = 2 * count - 1 - index;
      result[i] += weights[k + radius] * values[index];
    }
  }
  return result;
}

inline double baseThreePoint(double wavelength, const std::array<double, 3> &peaks, const std::array<double, 3> &values) {
  if (wavelength <= peaks[0]) return values[0];
  if (wavelength >= peaks[2]) return values[2];
  const unsigned lo = wavelength < peaks[1] ? 0 : 1;
  const double t = (wavelength - peaks[lo]) / (peaks[lo + 1] - peaks[lo]);
  return values[lo] * (1.0 - t) + values[lo + 1] * t;
}

inline double sampleBaseSpectrum(const ProfileCurveSet &curves, double wavelength) {
  const auto count = curves.wavelengthCount;
  if (wavelength <= curves.wavelengths[0]) return curves.baseDensity[0];
  if (wavelength >= curves.wavelengths[count - 1]) return curves.baseDensity[count - 1];
  const auto upper = std::upper_bound(curves.wavelengths, curves.wavelengths + count, wavelength);
  const auto hi = static_cast<size_t>(upper - curves.wavelengths), lo = hi - 1;
  const double t = (wavelength - curves.wavelengths[lo]) / (curves.wavelengths[hi] - curves.wavelengths[lo]);
  return curves.baseDensity[lo] * (1.0 - t) + curves.baseDensity[hi] * t;
}

inline std::vector<float> makeFilmBaseDensity(const ProfileCurveSet &curves, const FilmBaseKey &key) {
  std::vector<float> result(curves.baseDensity, curves.baseDensity + curves.wavelengthCount);
  if (key == kNeutralFilmBase) return result;
  const bool neutralShape = key[1] == 0 && key[2] == 1 && key[3] == 1 && key[4] == 1;
  if (neutralShape) {
    for (auto &value : result) value *= key[0];
    return result;
  }
  const double spacing = (curves.wavelengths[curves.wavelengthCount - 1] - curves.wavelengths[0]) / (curves.wavelengthCount - 1);
  std::vector<double> color(curves.wavelengthCount), tilt(curves.wavelengthCount);
  for (uint32_t i = 0; i < curves.wavelengthCount; ++i) {
    color[i] = baseThreePoint(curves.wavelengths[i], {460, 555, 650}, {key[4], key[3], key[2]});
    tilt[i] = std::max(0.0, 1.0 + key[1] * (curves.wavelengths[i] - 555.0) / 95.0);
  }
  color = baseGaussianReflect(color, 35.0 / spacing);
  tilt = baseGaussianReflect(tilt, 35.0 / spacing);
  for (uint32_t i = 0; i < curves.wavelengthCount; ++i)
    // Apply scale once. DW 2.2.3 inadvertently applies it twice only when
    // tint/tilt are nonneutral, causing an epsilon-sized edit to jump in density.
    result[i] = static_cast<float>(curves.baseDensity[i] * key[0] * color[i] * tilt[i]);
  return result;
}

inline std::vector<float> makePrintBaseDensity(const ProfileCurveSet &curves, const PrintBaseKey &key) {
  std::vector<float> result(curves.baseDensity, curves.baseDensity + curves.wavelengthCount);
  if (key == kNeutralPrintBase) return result;
  if (key[1] == 1 && key[2] == 1 && key[3] == 1) {
    for (auto &value : result) value *= key[0];
    return result;
  }
  const std::array<double, 3> peaks{445, 530, 610};
  const std::array<double, 3> neutral{sampleBaseSpectrum(curves, peaks[0]), sampleBaseSpectrum(curves, peaks[1]), sampleBaseSpectrum(curves, peaks[2])};
  const std::array<double, 3> custom{neutral[0] * key[3], neutral[1] * key[2], neutral[2] * key[1]};
  const double spacing = (curves.wavelengths[curves.wavelengthCount - 1] - curves.wavelengths[0]) / (curves.wavelengthCount - 1);
  std::vector<double> originalShape(curves.wavelengthCount), customShape(curves.wavelengthCount);
  for (uint32_t i = 0; i < curves.wavelengthCount; ++i) {
    originalShape[i] = baseThreePoint(curves.wavelengths[i], peaks, neutral);
    customShape[i] = baseThreePoint(curves.wavelengths[i], peaks, custom);
  }
  originalShape = baseGaussianReflect(originalShape, 20.0 / spacing);
  customShape = baseGaussianReflect(customShape, 20.0 / spacing);
  for (uint32_t i = 0; i < curves.wavelengthCount; ++i) {
    // Transfer the DW spectral tint as a nonnegative relative shape. Retain
    // the original base spectrum rather than replacing it at the first tint
    // change. If all reference peaks are zero, no tint ratio can be inferred.
    const double ratio = originalShape[i] > 0.0 ? customShape[i] / originalShape[i] : 1.0;
    result[i] = static_cast<float>(curves.baseDensity[i] * key[0] * ratio);
  }
  return result;
}

} // namespace spektrafilm
