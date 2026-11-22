#pragma once

// Port of resolve_grain_model from The Dyeworks 2.2.3 model/grain.py.
// Celian Le Bolloch / Andrea Volpato, GPL-3.0-or-later. Port modified 2026-11-16.
// See THIRD-PARTY.md. This only calibrates the NEW RMS
// model; it never changes the existing Production/Preview/Synthesis parameters.
#include "SpektraParameters.h"
#include "SpektraProfileCurves.h"
#include "SpektraGrainRmsPresets.generated.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <stdexcept>
#include <vector>

namespace spektrafilm {

// Inputs are the same effective net-density curves uploaded to the renderer,
// including any explicit aging transform. Output is the per-channel particle
// area in square micrometres (Production area=1, channel scales=these values).
inline std::array<double, 3> resolveGrainRms(const RenderParams &p, const ProfileCurveSet &curves) {
  if (!curves.densityCurves || curves.exposureCount == 0)
    throw std::invalid_argument("RMS grain requires sampled density curves");
  const auto targets = p.grainRmsUseStock ? stockGrainRms(curves.stock)
    : std::array<double, 3>{p.grainRmsR, p.grainRmsG, p.grainRmsB};
  const double mins[] = {p.grainDensityMinR, p.grainDensityMinG, p.grainDensityMinB};
  const double uniformity[] = {p.grainUniformityR, p.grainUniformityG, p.grainUniformityB};
  const double layerScale[] = {p.grainParticleScaleLayer0, p.grainParticleScaleLayer1, p.grainParticleScaleLayer2};
  constexpr double apertureArea = 3.14159265358979323846 * 24.0 * 24.0;
  const bool useLayers = p.grainSublayersEnabled && curves.densityCurveLayers;
  std::array<double, 3> areas{};
  for (unsigned ch = 0; ch < 3; ++ch) {
    std::vector<unsigned> order;
    for (unsigned i = 0; i < curves.exposureCount; ++i)
      if (std::isfinite(curves.densityCurves[i * 3 + ch])) order.push_back(i);
    if (order.empty()) throw std::invalid_argument("RMS grain requires finite density curves");
    std::stable_sort(order.begin(), order.end(), [&](unsigned a, unsigned b) {
      return curves.densityCurves[a * 3 + ch] < curves.densityCurves[b * 3 + ch];
    });
    double denom = 0.0;
    if (useLayers) {
      double maxima[3] = {};
      for (unsigned layer = 0; layer < 3; ++layer) {
        maxima[layer] = -INFINITY;
        for (unsigned i = 0; i < curves.exposureCount; ++i) {
          const double value = curves.densityCurveLayers[i * 9 + layer * 3 + ch];
          if (!std::isnan(value)) maxima[layer] = std::max(maxima[layer], value);
        }
      }
      const double totalMax = std::max(maxima[0] + maxima[1] + maxima[2], 1e-9);
      for (unsigned layer = 0; layer < 3; ++layer) {
        const double fraction = maxima[layer] / totalMax;
        const double layerMin = fraction * mins[ch];
        const double adjustedMax = maxima[layer] + layerMin;
        if (!(adjustedMax > 0) || !std::isfinite(adjustedMax))
          throw std::invalid_argument("RMS grain requires positive finite layer maxima");
        const auto layerAt = [&](unsigned i) { return double(curves.densityCurveLayers[i * 9 + layer * 3 + ch]); };
        // np.interp(1, sorted total, corresponding layer), including endpoints
        // and the last sample of a duplicate total-density value.
        const auto upper = std::upper_bound(order.begin(), order.end(), 1.0,
          [&](double value, unsigned i) { return value < curves.densityCurves[i * 3 + ch]; });
        double layerDensity;
        if (upper == order.begin()) layerDensity = layerAt(order.front());
        else if (upper == order.end()) layerDensity = layerAt(order.back());
        else {
          const unsigned hi = *upper, lo = *(upper - 1);
          const double x0 = curves.densityCurves[lo * 3 + ch], x1 = curves.densityCurves[hi * 3 + ch];
          layerDensity = layerAt(lo) + (1.0 - x0) * (layerAt(hi) - layerAt(lo)) / (x1 - x0);
        }
        const double probability = std::clamp((layerDensity + layerMin) / adjustedMax, 1e-6, 1.0 - 1e-6);
        denom += adjustedMax * adjustedMax * layerScale[layer] * probability
          * (1.0 - probability * uniformity[ch]) / std::max(fraction, 1e-9);
      }
    } else {
      const double maximum = curves.densityCurves[order.back() * 3 + ch] + mins[ch];
      if (!(maximum > 0)) throw std::invalid_argument("RMS grain requires positive density maxima");
      const double probability = std::clamp((1.0 + mins[ch]) / maximum, 1e-6, 1.0 - 1e-6);
      denom = maximum * maximum * probability * (1.0 - probability * uniformity[ch]);
    }
    const double target = std::max(targets[ch], 0.1) / 1000.0 * std::max(double(p.grainRmsStrength), 1e-3);
    areas[ch] = target * target * apertureArea / std::max(denom, 1e-12);
    if (!std::isfinite(areas[ch])) throw std::invalid_argument("RMS grain calibration is not finite");
  }
  return areas;
}
} // namespace spektrafilm
