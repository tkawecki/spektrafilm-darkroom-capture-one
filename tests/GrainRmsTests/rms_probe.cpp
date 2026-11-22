#include "SpektraGrainRms.h"
#include <iostream>
#include <iomanip>
#include <vector>
#include <string>

int main() {
  using namespace spektrafilm;
  std::cout << std::setprecision(17);
  std::string stock;
  while (std::cin >> stock) {
    RenderParams p;
    ProfileCurveSet c;
    c.stock = stock.c_str();
    p.grainModel = GrainModel::RmsCalibrated;
    std::cin >> c.exposureCount >> p.grainSublayersEnabled >> p.grainRmsUseStock
             >> p.grainRmsR >> p.grainRmsG >> p.grainRmsB >> p.grainRmsStrength
             >> p.grainDensityMinR >> p.grainDensityMinG >> p.grainDensityMinB
             >> p.grainUniformityR >> p.grainUniformityG >> p.grainUniformityB
             >> p.grainParticleScaleLayer0 >> p.grainParticleScaleLayer1 >> p.grainParticleScaleLayer2;
    std::vector<float> total(c.exposureCount * 3), layers(c.exposureCount * 9);
    for (auto &value : total) std::cin >> value;
    for (auto &value : layers) std::cin >> value;
    c.densityCurves = total.data(); c.densityCurveLayers = layers.data();
    const auto areas = resolveGrainRms(p, c);
    std::cout << areas[0] << ' ' << areas[1] << ' ' << areas[2] << '\n';
  }
}
