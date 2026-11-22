#include "SpektraFilmAging.h"
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <string>

template<typename Value>
void array(const Value *data, size_t count) {
  std::cout << '[';
  for (size_t i = 0; i < count; ++i) std::cout << (i ? "," : "") << data[i];
  std::cout << ']';
}

int main(int argc, char **argv) {
  if (argc != 15) return 2;
  const int stock = std::atoi(argv[1]);
  auto *curves = spektrafilm::filmProfileCurves(stock);
  spektrafilm::ProfileCurveSet synthetic;
  const float x[] = {0, 1, 2, 3};
  const float total[] = {0,0,0, 1,1,1, 2,2,2, 3,3,3};
  float layers[36];
  if (stock < 0) {
    for (unsigned i = 0; i < 4; ++i)
      for (unsigned layer = 0; layer < 3; ++layer)
        for (unsigned c = 0; c < 3; ++c) layers[i * 9 + layer * 3 + c] = i * (layer == 0 ? 0.2f : layer == 1 ? 0.3f : 0.5f);
    synthetic.logExposure = x;
    synthetic.densityCurves = total;
    synthetic.densityCurveLayers = layers;
    synthetic.exposureCount = 4;
    curves = &synthetic;
  }
  if (!curves) return 3;
  spektrafilm::RenderParams p;
  p.filmAgingEnabled = std::atoi(argv[2]) != 0;
  p.filmAgeYears = std::stof(argv[3]); p.filmAgeStorage = static_cast<spektrafilm::FilmAgeStorage>(std::atoi(argv[4]));
  p.filmAgeFogR = std::stof(argv[5]); p.filmAgeFogG = std::stof(argv[6]); p.filmAgeFogB = std::stof(argv[7]);
  p.filmAgeGamma = std::stof(argv[8]);
  p.filmAgeGammaR = std::stof(argv[9]); p.filmAgeGammaG = std::stof(argv[10]); p.filmAgeGammaB = std::stof(argv[11]);
  p.filmAgeDensityScale = std::stof(argv[12]); p.filmAgeGrainGain = std::stof(argv[13]); p.filmAgeSpeedLossEv = std::stof(argv[14]);
  const auto e = spektrafilm::resolvedFilmAging(p);
  const auto aged = spektrafilm::makeAgedFilmCurves(*curves, e);
  const double effect[] = {e.fog[0],e.fog[1],e.fog[2],e.gamma,e.gammaRgb[0],e.gammaRgb[1],e.gammaRgb[2],e.densityScale,e.grainGain,e.speedLossEv};
  std::cout << std::setprecision(17) << "{\"effect\":";
  array(effect, 10); std::cout << ",\"x\":";
  array(curves->logExposure, curves->exposureCount); std::cout << ",\"before_total\":";
  array(curves->densityCurves, curves->exposureCount * 3); std::cout << ",\"after_total\":";
  array(aged.density.data(), aged.density.size()); std::cout << ",\"before_layers\":";
  array(curves->densityCurveLayers, curves->exposureCount * 9); std::cout << ",\"after_layers\":";
  array(aged.layers.data(), aged.layers.size()); std::cout << ",\"layer_maxima\":";
  array(aged.layerMaxima.data(), 9); std::cout << '}';
}
