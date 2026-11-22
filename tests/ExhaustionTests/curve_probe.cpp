#include "SpektraPrintCurvesMorph.h"
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <string>

int main(int argc, char **argv) {
  if (argc != 4) return 2;
  const auto *curves = spektrafilm::paperProfileCurves(std::atoi(argv[1]));
  const auto *model = spektrafilm::paperPrintCurveModel(curves->stock);
  const float gamma = std::stof(argv[2]), exhaustion = std::stof(argv[3]);
  const spektrafilm::PrintCurvesMorph baseline(*model, gamma, 0.0);
  const spektrafilm::PrintCurvesMorph morphed(*model, gamma, exhaustion);
  const auto adjusted = spektrafilm::makePrintExhaustionDensityCurves(*curves, *model, gamma, exhaustion);
  std::cout << std::setprecision(17);
  for (uint32_t sample = 0; sample < curves->exposureCount + 3u; ++sample) {
    const bool grid = sample < curves->exposureCount;
    const double x = grid ? static_cast<double>(curves->logExposure[sample]) / gamma
                         : (static_cast<int>(sample - curves->exposureCount) - 1) * 1000.0;
    for (unsigned c = 0; c < 3; ++c)
      std::cout << x << ',' << c << ',' << baseline.density(x, c) << ',' << morphed.density(x, c)
                << ',' << (grid ? curves->densityCurves[sample * 3u + c] : spektrafilm::sampleLegacyPrintCurve(*curves, x * gamma, c))
                << ',' << (grid ? adjusted[sample * 3u + c] : spektrafilm::printExhaustionDensityAtExposure(*curves, baseline, morphed, gamma, x, c)) << '\n';
  }
}
