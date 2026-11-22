#pragma once

// Native adaptation of The Dyeworks 2.2.3 utils/expiration.py (GPL-3.0,
// Andrea Volpato / Celian Le Bolloch). The generic age/storage constants are
// an artistic empirical model, not measured stock-specific aging laws.
#include "SpektraParameters.h"
#include "SpektraProfileCurves.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <vector>

namespace spektrafilm {

using FilmAgingKey = std::array<float, 12>;
inline constexpr FilmAgingKey kNeutralFilmAging{0, 2, 0, 0, 0, 1, 1, 1, 1, 1, 1, 0};
inline FilmAgingKey filmAgingKey(const RenderParams &p) {
  return p.filmAgingEnabled ? FilmAgingKey{p.filmAgeYears, static_cast<float>(p.filmAgeStorage),
    p.filmAgeFogR, p.filmAgeFogG, p.filmAgeFogB, p.filmAgeGamma,
    p.filmAgeGammaR, p.filmAgeGammaG, p.filmAgeGammaB,
    p.filmAgeDensityScale, p.filmAgeGrainGain, p.filmAgeSpeedLossEv} : kNeutralFilmAging;
}

struct FilmAgingEffect {
  std::array<double, 3> fog{0, 0, 0};
  double gamma = 1;
  std::array<double, 3> gammaRgb{1, 1, 1};
  double densityScale = 1;
  double grainGain = 1;
  double speedLossEv = 0;

  bool changesCurves() const {
    return fog != std::array<double, 3>{0, 0, 0} || gamma != 1 ||
      gammaRgb != std::array<double, 3>{1, 1, 1} || densityScale != 1 || speedLossEv != 0;
  }
};

inline FilmAgingEffect resolvedFilmAging(const RenderParams &p) {
  FilmAgingEffect result;
  if (!p.filmAgingEnabled) return result;
  constexpr double rates[] = {0.05, 0.3, 1, 2.5};
  const int storage = std::clamp(static_cast<int>(p.filmAgeStorage), 0, 3);
  const double effectiveYears = std::max(static_cast<double>(p.filmAgeYears), 0.0) * rates[storage];
  const double x = 1.0 - std::exp(-effectiveYears / 12.0);
  const double extraFog[] = {p.filmAgeFogR, p.filmAgeFogG, p.filmAgeFogB};
  const double fogFull[] = {0.10, 0.14, 0.22};
  const double extraGamma[] = {p.filmAgeGammaR, p.filmAgeGammaG, p.filmAgeGammaB};
  const double crossover[] = {0.04, 0.0, -0.10};
  for (unsigned c = 0; c < 3; ++c) {
    result.fog[c] = std::max(x * fogFull[c] + extraFog[c], 0.0);
    result.gammaRgb[c] = std::max((1.0 + crossover[c] * x) * std::max(extraGamma[c], 0.05), 0.05);
  }
  result.gamma = std::max((1.0 - 0.18 * x) * std::max(static_cast<double>(p.filmAgeGamma), 0.05), 0.05);
  result.densityScale = std::max((1.0 - 0.22 * x) * std::clamp(static_cast<double>(p.filmAgeDensityScale), 0.05, 2.0), 0.05);
  result.grainGain = std::max((1.0 + 0.6 * x) * std::clamp(static_cast<double>(p.filmAgeGrainGain), 0.05, 8.0), 0.05);
  result.speedLossEv = std::min(0.1 * effectiveYears, 3.0) + p.filmAgeSpeedLossEv;
  return result;
}

template<typename Value>
inline double agingSample(const float *exposure, const Value *values, uint32_t count,
                          uint32_t stride, uint32_t component, double x) {
  if (x <= exposure[0]) return values[component];
  if (x >= exposure[count - 1]) return values[(count - 1) * stride + component];
  const auto upper = std::upper_bound(exposure, exposure + count, x);
  const auto hi = static_cast<uint32_t>(upper - exposure), lo = hi - 1;
  const double t = (x - exposure[lo]) / (static_cast<double>(exposure[hi]) - exposure[lo]);
  return values[lo * stride + component] * (1 - t) + values[hi * stride + component] * t;
}

struct AgedFilmCurves {
  std::vector<float> density;
  std::vector<float> layers;
  std::array<float, 9> layerMaxima{};
};

inline AgedFilmCurves makeAgedFilmCurves(const ProfileCurveSet &curves, const FilmAgingEffect &effect) {
  const auto count = curves.exposureCount;
  AgedFilmCurves result;
  result.density.assign(curves.densityCurves, curves.densityCurves + count * 3u);
  if (curves.densityCurveLayers) result.layers.assign(curves.densityCurveLayers, curves.densityCurveLayers + count * 9u);
  if (curves.densityCurveLayerMaxima)
    std::copy(curves.densityCurveLayerMaxima, curves.densityCurveLayerMaxima + 9u, result.layerMaxima.begin());
  if (!effect.changesCurves()) return result;
  std::vector<double> totalGamma(count * 3u), layerGamma(result.layers.size());
  for (uint32_t i = 0; i < count; ++i) {
    for (unsigned c = 0; c < 3; ++c) {
      // Preserve the existing sampled profile. Coupled gamma is an exposure
      // coordinate scaling; do not replace the stock with a newer fitted set.
      const double x = curves.logExposure[i] * effect.gamma * effect.gammaRgb[c];
      totalGamma[i * 3u + c] = agingSample(curves.logExposure, curves.densityCurves, count, 3, c, x);
      for (unsigned layer = 0; layer < (result.layers.empty() ? 0u : 3u); ++layer)
        layerGamma[i * 9u + layer * 3u + c] = agingSample(curves.logExposure, curves.densityCurveLayers, count, 9, layer * 3u + c, x);
    }
  }
  constexpr double kLog10Two = 0.3010299956639811952;
  const double delta = effect.speedLossEv * kLog10Two;
  for (unsigned c = 0; c < 3; ++c) {
    double maximum = 0;
    std::array<double, 3> amplitudes{};
    bool consistent = !result.layers.empty();
    for (uint32_t i = 0; i < count; ++i) {
      maximum = std::max(maximum, totalGamma[i * 3u + c]);
      double sum = 0;
      for (unsigned layer = 0; layer < (result.layers.empty() ? 0u : 3u); ++layer) {
        const double value = layerGamma[i * 9u + layer * 3u + c];
        amplitudes[layer] = std::max(amplitudes[layer], value);
        sum += value;
      }
      consistent = consistent && std::abs(totalGamma[i * 3u + c] - sum) <= 1e-9 + 1e-5 * std::abs(sum);
    }
    const double amplitudeSum = amplitudes[0] + amplitudes[1] + amplitudes[2];
    const double layerFog = std::min(effect.fog[c], 0.9 * amplitudeSum);
    const double totalFog = std::min(effect.fog[c], 0.9 * maximum);
    for (uint32_t i = 0; i < count; ++i) {
      const double x = curves.logExposure[i] - delta;
      double layerSum = 0;
      for (unsigned layer = 0; layer < (result.layers.empty() ? 0u : 3u); ++layer) {
        double value = agingSample(curves.logExposure, layerGamma.data(), count, 9, layer * 3u + c, x);
        if (amplitudes[layer] > 0 && layerFog > 0)
          value += layerFog * (amplitudes[layer] / amplitudeSum) * (1 - value / amplitudes[layer]);
        value *= effect.densityScale;
        result.layers[i * 9u + layer * 3u + c] = static_cast<float>(value);
        layerSum += value;
      }
      double value = agingSample(curves.logExposure, totalGamma.data(), count, 3, c, x);
      if (maximum > 0 && totalFog > 0) value += totalFog * (1 - value / maximum);
      result.density[i * 3u + c] = static_cast<float>(consistent ? layerSum : value * effect.densityScale);
    }
  }
  if (!result.layers.empty()) {
    result.layerMaxima.fill(0);
    for (uint32_t i = 0; i < count; ++i)
      for (unsigned component = 0; component < 9; ++component)
        result.layerMaxima[component] = std::max(result.layerMaxima[component], result.layers[i * 9u + component]);
  }
  return result;
}

} // namespace spektrafilm
