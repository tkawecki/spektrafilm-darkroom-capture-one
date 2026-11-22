#include "SpektraBaseDensity.h"
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <string>

int main(int argc, char **argv) {
  if (argc != 8) return 2;
  const bool film = std::atoi(argv[1]) == 0;
  const auto *curves = film ? spektrafilm::filmProfileCurves(std::atoi(argv[2])) : spektrafilm::paperProfileCurves(std::atoi(argv[2]));
  if (!curves) return 3;
  const float scale = std::stof(argv[3]), cyan = std::stof(argv[4]), magenta = std::stof(argv[5]), yellow = std::stof(argv[6]), tilt = std::stof(argv[7]);
  const auto tuned = film ? spektrafilm::makeFilmBaseDensity(*curves, {scale, tilt, cyan, magenta, yellow})
                         : spektrafilm::makePrintBaseDensity(*curves, {scale, cyan, magenta, yellow});
  std::cout << std::setprecision(17);
  for (uint32_t i = 0; i < curves->wavelengthCount; ++i)
    std::cout << curves->wavelengths[i] << ',' << curves->baseDensity[i] << ',' << tuned[i] << '\n';
}
