#pragma once

// Native adaptation of pinned spektrafilm/utils/morph_curves.py, Andrea
// Volpato. Preserve the upstream code/data licenses and attribution.
#include "SpektraGeneratedPrintCurveModels.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <stdexcept>
#include <vector>

namespace spektrafilm {

class PrintCurvesMorph {
public:
  PrintCurvesMorph(const PrintCurveModel &model, double gamma, double exhaustion)
    : model_(model), exhaustion_(exhaustion) {
    if (!std::isfinite(gamma) || gamma <= 0.0 || !std::isfinite(exhaustion) ||
        exhaustion < 0.0 || exhaustion > 1.0)
      throw std::invalid_argument("Invalid print curve gamma or developer exhaustion.");
    for (unsigned channel = 0; channel < 3; ++channel) {
      for (unsigned layer = 0; layer < 3; ++layer) {
        centers_[channel][layer] = model.centers[channel][layer] / gamma;
        sigmas_[channel][layer] = std::max(model.sigmas[channel][layer] / gamma, 0.05);
      }
      offsets_[channel] = centerOffset(channel);
    }
  }

  double density(double logExposure, unsigned channel) const {
    return evaluate(logExposure, channel, offsets_[channel], exhaustion_);
  }

  double densityComplement(double logExposure, unsigned channel) const {
    return evaluate(logExposure, channel, offsets_[channel], exhaustion_, true);
  }

  double exposureAtDensity(double target, double targetComplement, unsigned channel, double lo, double hi) const {
    // Invert the smaller tail, avoiding Dmax-CDF cancellation in the shoulder.
    // This keeps the exposure warp continuous even for exhaustion near zero.
    const bool complement = target > targetComplement;
    const double value = complement ? targetComplement : target;
    const double sign = (model_.positive ? -1.0 : 1.0) * (complement ? -1.0 : 1.0);
    const auto at = [&](double x) { return evaluate(x, channel, offsets_[channel], exhaustion_, complement); };
    if (sign * value <= sign * at(lo)) return lo;
    if (sign * value >= sign * at(hi)) return hi;
    if (std::abs(value - at(0.0)) <= 1.0e-12) return 0.0;
    for (unsigned step = 0; step < 60 && hi - lo > 1.0e-13; ++step) {
      const double mid = (lo + hi) * 0.5;
      if (sign * at(mid) < sign * value) lo = mid;
      else hi = mid;
    }
    return (lo + hi) * 0.5;
  }

private:
  double evaluate(double x, unsigned channel, double offset, double mix, bool complement = false) const {
    constexpr double kSqrtTwo = 1.4142135623730950488;
    // Median and slope at z=0 match the normal CDF (pinned s023 model).
    constexpr double kGumbelLocation = 0.3665129205816643270;
    constexpr double kGumbelWidth = 0.8687311606361591417;
    double density = 0.0;
    for (unsigned layer = 0; layer < 3; ++layer) {
      double z = (x - centers_[channel][layer] - offset) / sigmas_[channel][layer];
      if (model_.positive) z = -z;
      double cdf = 0.5 * std::erfc((complement ? z : -z) / kSqrtTwo);
      if (mix > 0.0) {
        const double inner = -(z / kGumbelWidth + kGumbelLocation);
        const double gumbel = inner > 709.0 ? (complement ? 1.0 : 0.0)
          : (complement ? -std::expm1(-std::exp(inner)) : std::exp(-std::exp(inner)));
        cdf = (1.0 - mix) * cdf + mix * gumbel;
      }
      density += model_.amplitudes[channel][layer] * cdf;
    }
    return density;
  }

  double centerOffset(unsigned channel) const {
    // np.allclose(mix, 0) in the reference uses atol=1e-8.
    if (exhaustion_ <= 1.0e-8) return 0.0;
    const double target = evaluate(0.0, channel, 0.0, 0.0);
    const auto residual = [&](double offset) {
      return evaluate(0.0, channel, offset, exhaustion_) - target;
    };
    if (std::abs(residual(0.0)) <= 1.0e-12) return 0.0;
    double lo = -0.25, hi = 0.25;
    for (unsigned expansion = 0; expansion < 12; ++expansion) {
      double rLo = residual(lo);
      const double rHi = residual(hi);
      if (rLo == 0.0) return lo;
      if (rHi == 0.0) return hi;
      if (rLo * rHi < 0.0) {
        // Monotone bisection is deterministic and tighter than the reference's
        // Brent xtol=1e-10, without an additional runtime dependency.
        for (unsigned step = 0; step < 60 && hi - lo > 1.0e-13; ++step) {
          const double mid = (lo + hi) * 0.5;
          const double rMid = residual(mid);
          if (rMid == 0.0) return mid;
          if ((rMid < 0.0) == (rLo < 0.0)) { lo = mid; rLo = rMid; }
          else hi = mid;
        }
        return (lo + hi) * 0.5;
      }
      lo *= 2.0;
      hi *= 2.0;
    }
    return 0.0;
  }

  const PrintCurveModel &model_;
  double exhaustion_;
  double centers_[3][3]{};
  double sigmas_[3][3]{};
  std::array<double, 3> offsets_{};
};

inline float sampleLegacyPrintCurve(const ProfileCurveSet &curves, double x, unsigned channel) {
  const auto count = curves.exposureCount;
  if (x <= curves.logExposure[0]) return curves.densityCurves[channel];
  if (x >= curves.logExposure[count - 1u]) return curves.densityCurves[(count - 1u) * 3u + channel];
  const auto upper = std::upper_bound(curves.logExposure, curves.logExposure + count, x);
  const auto hi = static_cast<uint32_t>(upper - curves.logExposure), lo = hi - 1u;
  const double t = (x - curves.logExposure[lo]) / (curves.logExposure[hi] - curves.logExposure[lo]);
  return static_cast<float>(curves.densityCurves[lo * 3u + channel] * (1.0 - t) +
                            curves.densityCurves[hi * 3u + channel] * t);
}

inline float printExhaustionDensityAtExposure(
  const ProfileCurveSet &curves, const PrintCurvesMorph &baseline,
  const PrintCurvesMorph &morphed, double gamma, double rawExposure, unsigned channel
) {
  const double warped = baseline.exposureAtDensity(morphed.density(rawExposure, channel),
    morphed.densityComplement(rawExposure, channel), channel,
    curves.logExposure[0] / gamma, curves.logExposure[curves.exposureCount - 1u] / gamma);
  return sampleLegacyPrintCurve(curves, warped * gamma, channel);
}

inline std::vector<float> makePrintExhaustionDensityCurves(
  const ProfileCurveSet &curves, const PrintCurveModel &model,
  float gamma, float exhaustion
) {
  std::vector<float> result(curves.densityCurves, curves.densityCurves + curves.exposureCount * 3u);
  if (exhaustion == 0.0f) return result;
  const PrintCurvesMorph baseline(model, gamma, 0.0);
  const PrintCurvesMorph morphed(model, gamma, exhaustion);
  // Anchor the finite LUT's existing saturation endpoints. The fitted model's
  // asymptotic Dmin/Dmax are fixed, but its Gumbel tail reaches them more slowly
  // than a normal CDF; do not turn the LUT cutoff into a new density maximum.
  for (uint32_t sample = 1; sample + 1u < curves.exposureCount; ++sample) {
    // The shader still samples at logRaw*gamma. Evaluate the fitted morph at
    // x/gamma so gamma (including push/pull) is applied exactly once. Map its
    // density through the inverse fitted baseline and sample the legacy curve
    // there: this monotone exposure warp preserves its density range/shape,
    // unlike adding residuals from two independently fitted profile versions.
    const double x = static_cast<double>(curves.logExposure[sample]) / gamma;
    for (unsigned channel = 0; channel < 3; ++channel)
      result[sample * 3u + channel] = printExhaustionDensityAtExposure(curves, baseline, morphed, gamma, x, channel);
  }
  return result;
}

} // namespace spektrafilm
