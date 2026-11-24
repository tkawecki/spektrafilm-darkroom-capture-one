#pragma once

// Spektrafilm C1 film model 4 (2026-11-24): auto exposure metered as the Python spektrafilm reference
// (spektrafilm.utils.autoexposure.measure_autoexposure_ev, GPLv3, Andrea Volpato) on its small preview
// (spektrafilm.runtime.services.resize.ResizeService.small_preview: skimage.transform.rescale to a 256 px long edge,
// order 0, which smooths with a Gaussian of sigma (factor - 1) / 2 before taking the pixel nearest to each output
// pixel centre). Models 1-3 keep the renderer's earlier metering (SpektraVulkanRenderer.cpp measureAutoExposureEv).

#include "SpektraParameters.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <system_error>
#include <thread>
#include <vector>

namespace spektrafilm::autoexposure {

constexpr int32_t kPreviewMaxSize = 256;
constexpr double kMidGray = 0.184;

struct Preview {
  int32_t width = 0;
  int32_t height = 0;
  std::vector<double> rgb;  // 3 channels per pixel, row-major
};

// scipy.ndimage 'mirror' boundary (skimage mode 'reflect'): d c b | a b c d | c b a.
inline int32_t mirrorIndex(int64_t index, int32_t size) {
  if (size <= 1) {
    return 0;
  }
  const int64_t period = 2 * static_cast<int64_t>(size) - 2;
  int64_t wrapped = index % period;
  if (wrapped < 0) {
    wrapped += period;
  }
  return static_cast<int32_t>(wrapped < size ? wrapped : period - wrapped);
}

// scipy.ndimage._gaussian_kernel1d with truncate 4.
inline std::vector<double> gaussianKernel(double sigma) {
  if (!(sigma > 0.0)) {
    return {1.0};
  }
  const int32_t radius = static_cast<int32_t>(4.0 * sigma + 0.5);
  std::vector<double> kernel(static_cast<size_t>(2 * radius + 1));
  double sum = 0.0;
  for (int32_t x = -radius; x <= radius; ++x) {
    const double value = std::exp(-0.5 / (sigma * sigma) * static_cast<double>(x) * static_cast<double>(x));
    kernel[static_cast<size_t>(x + radius)] = value;
    sum += value;
  }
  for (double &value : kernel) {
    value /= sum;
  }
  return kernel;
}

// Output size of skimage rescale: max(round(scale * size), 1), numpy rounding half to even.
inline int32_t rescaledSize(int32_t size, double scale) {
  return std::max(1, static_cast<int32_t>(std::nearbyint(scale * static_cast<double>(size))));
}

// Source index sampled for an output index by scipy.ndimage.zoom(order=0, grid_mode=True).
inline int32_t nearestSourceIndex(int32_t outIndex, double factor, int32_t size) {
  const double coordinate = (static_cast<double>(outIndex) + 0.5) * factor - 0.5;
  return std::clamp(static_cast<int32_t>(std::floor(coordinate + 0.5)), 0, size - 1);
}

// The Python small preview of an RGBA float image. The Gaussian is evaluated only at the sampled pixels: rows are
// filtered at the sampled columns (in parallel over rows), then the sampled rows are filtered.
inline Preview referencePreview(const float *rgba, int32_t width, int32_t height) {
  Preview preview;
  const int32_t longEdge = std::max(width, height);
  if (longEdge <= kPreviewMaxSize) {
    preview.width = width;
    preview.height = height;
    preview.rgb.resize(static_cast<size_t>(width) * static_cast<size_t>(height) * 3u);
    for (size_t i = 0; i < static_cast<size_t>(width) * static_cast<size_t>(height); ++i) {
      for (size_t c = 0; c < 3u; ++c) {
        preview.rgb[i * 3u + c] = rgba[i * 4u + c];
      }
    }
    return preview;
  }
  const double scale = static_cast<double>(kPreviewMaxSize) / static_cast<double>(longEdge);
  preview.width = rescaledSize(width, scale);
  preview.height = rescaledSize(height, scale);
  const double factorX = static_cast<double>(width) / static_cast<double>(preview.width);
  const double factorY = static_cast<double>(height) / static_cast<double>(preview.height);
  const std::vector<double> kernelX = gaussianKernel(std::max(0.0, (factorX - 1.0) / 2.0));
  const std::vector<double> kernelY = gaussianKernel(std::max(0.0, (factorY - 1.0) / 2.0));
  const int32_t radiusX = static_cast<int32_t>(kernelX.size() / 2u);
  const int32_t radiusY = static_cast<int32_t>(kernelY.size() / 2u);
  std::vector<int32_t> columns(static_cast<size_t>(preview.width));
  for (int32_t x = 0; x < preview.width; ++x) {
    columns[static_cast<size_t>(x)] = nearestSourceIndex(x, factorX, width);
  }

  // Horizontal pass: every source row at the sampled columns.
  std::vector<double> rows(static_cast<size_t>(height) * static_cast<size_t>(preview.width) * 3u);
  const auto filterRows = [&](int32_t first, int32_t last) {
    for (int32_t y = first; y < last; ++y) {
      const float *row = rgba + static_cast<size_t>(y) * static_cast<size_t>(width) * 4u;
      for (int32_t x = 0; x < preview.width; ++x) {
        double sum[3] = {0.0, 0.0, 0.0};
        const int64_t center = columns[static_cast<size_t>(x)];
        for (int32_t k = -radiusX; k <= radiusX; ++k) {
          const float *pixel = row + static_cast<size_t>(mirrorIndex(center + k, width)) * 4u;
          const double weight = kernelX[static_cast<size_t>(k + radiusX)];
          sum[0] += weight * pixel[0];
          sum[1] += weight * pixel[1];
          sum[2] += weight * pixel[2];
        }
        double *out = rows.data() + (static_cast<size_t>(y) * static_cast<size_t>(preview.width) + static_cast<size_t>(x)) * 3u;
        out[0] = sum[0];
        out[1] = sum[1];
        out[2] = sum[2];
      }
    }
  };
  const int32_t threads = std::clamp(static_cast<int32_t>(std::thread::hardware_concurrency()), 1, 16);
  if (threads <= 1 || height < 64) {
    filterRows(0, height);
  } else {
    std::vector<std::thread> workers;
    const int32_t chunk = (height + threads - 1) / threads;
    int32_t next = 0;
    try {
      for (; next < height; next += chunk) {
        workers.emplace_back(filterRows, next, std::min(height, next + chunk));
      }
    } catch (const std::system_error &) {
      // No more threads: the rows not handed out are filtered here.
    }
    filterRows(next, height);
    for (std::thread &worker : workers) {
      worker.join();
    }
  }

  // Vertical pass at the sampled rows.
  preview.rgb.resize(static_cast<size_t>(preview.width) * static_cast<size_t>(preview.height) * 3u);
  for (int32_t y = 0; y < preview.height; ++y) {
    const int64_t center = nearestSourceIndex(y, factorY, height);
    for (int32_t x = 0; x < preview.width; ++x) {
      double sum[3] = {0.0, 0.0, 0.0};
      for (int32_t k = -radiusY; k <= radiusY; ++k) {
        const double *pixel =
          rows.data() + (static_cast<size_t>(mirrorIndex(center + k, height)) * static_cast<size_t>(preview.width) + static_cast<size_t>(x)) * 3u;
        const double weight = kernelY[static_cast<size_t>(k + radiusY)];
        sum[0] += weight * pixel[0];
        sum[1] += weight * pixel[1];
        sum[2] += weight * pixel[2];
      }
      double *out = preview.rgb.data() + (static_cast<size_t>(y) * static_cast<size_t>(preview.width) + static_cast<size_t>(x)) * 3u;
      out[0] = sum[0];
      out[1] = sum[1];
      out[2] = sum[2];
    }
  }
  return preview;
}

// Coordinates normalised so the long edge spans [-0.5, 0.5] (Python _normalized_coords).
inline void normalizedCoordinates(int32_t width, int32_t height, std::vector<double> &xs, std::vector<double> &ys) {
  const double longEdge = static_cast<double>(std::max(width, height));
  xs.resize(static_cast<size_t>(width));
  ys.resize(static_cast<size_t>(height));
  for (int32_t x = 0; x < width; ++x) {
    xs[static_cast<size_t>(x)] = (static_cast<double>(x) / width - 0.5) * (width / longEdge);
  }
  for (int32_t y = 0; y < height; ++y) {
    ys[static_cast<size_t>(y)] = (static_cast<double>(y) / height - 0.5) * (height / longEdge);
  }
}

inline double mean(const std::vector<double> &values) {
  double sum = 0.0;
  for (double value : values) {
    sum += value;
  }
  return values.empty() ? 0.0 : sum / static_cast<double>(values.size());
}

inline double median(std::vector<double> values) {
  if (values.empty()) {
    return 0.0;
  }
  const size_t mid = values.size() / 2u;
  std::nth_element(values.begin(), values.begin() + static_cast<std::ptrdiff_t>(mid), values.end());
  const double upper = values[mid];
  if ((values.size() & 1u) != 0u) {
    return upper;
  }
  const double lower = *std::max_element(values.begin(), values.begin() + static_cast<std::ptrdiff_t>(mid));
  return 0.5 * (lower + upper);
}

// The metered luminance of measure_autoexposure_ev for one method (before the division by mid grey).
inline double meterLuminance(const std::vector<double> &luminance, int32_t width, int32_t height,
                             AutoExposureMethod method) {
  std::vector<double> xs;
  std::vector<double> ys;
  normalizedCoordinates(width, height, xs, ys);
  const auto radius = [&](int32_t x, int32_t y) {
    return std::sqrt(xs[static_cast<size_t>(x)] * xs[static_cast<size_t>(x)] +
                     ys[static_cast<size_t>(y)] * ys[static_cast<size_t>(y)]);
  };
  const auto at = [&](int32_t x, int32_t y) {
    return luminance[static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)];
  };
  switch (method) {
    case AutoExposureMethod::Average:
      return mean(luminance);
    case AutoExposureMethod::Median:
      return median(luminance);
    case AutoExposureMethod::Partial: {
      double sum = 0.0;
      size_t count = 0;
      for (int32_t y = 0; y < height; ++y) {
        for (int32_t x = 0; x < width; ++x) {
          if (radius(x, y) < 0.15) {
            sum += at(x, y);
            ++count;
          }
        }
      }
      return count == 0u ? mean(luminance) : sum / static_cast<double>(count);
    }
    case AutoExposureMethod::Matrix: {
      constexpr int32_t kZones = 5;
      const int32_t cellHeight = height / kZones;
      const int32_t cellWidth = width / kZones;
      double weightedSum = 0.0;
      double weightTotal = 0.0;
      if (cellHeight <= 0 || cellWidth <= 0) {
        return mean(luminance);
      }
      for (int32_t r = 0; r < kZones; ++r) {
        for (int32_t c = 0; c < kZones; ++c) {
          double sum = 0.0;
          for (int32_t y = r * cellHeight; y < (r + 1) * cellHeight; ++y) {
            for (int32_t x = c * cellWidth; x < (c + 1) * cellWidth; ++x) {
              sum += at(x, y);
            }
          }
          const double zoneMean = sum / (static_cast<double>(cellHeight) * static_cast<double>(cellWidth));
          const double dy = (r - (kZones - 1) / 2.0) / ((kZones - 1) / 2.0);
          const double dx = (c - (kZones - 1) / 2.0) / ((kZones - 1) / 2.0);
          const double distance = std::sqrt(dx * dx + dy * dy) / std::sqrt(2.0);
          const double weight = 0.5 * (1.0 + std::cos(3.14159265358979323846 * distance));
          weightedSum += weight * zoneMean;
          weightTotal += weight;
        }
      }
      return weightedSum / weightTotal;
    }
    case AutoExposureMethod::MultiZone: {
      constexpr double kBounds[3][2] = {{0.00, 0.05}, {0.05, 0.25}, {0.25, 0.50}};
      constexpr double kWeights[3] = {0.50, 0.30, 0.20};
      double weightedSum = 0.0;
      double weightTotal = 0.0;
      for (int32_t ring = 0; ring < 3; ++ring) {
        double sum = 0.0;
        size_t count = 0;
        for (int32_t y = 0; y < height; ++y) {
          for (int32_t x = 0; x < width; ++x) {
            const double r = radius(x, y);
            if (r >= kBounds[ring][0] && r < kBounds[ring][1]) {
              sum += at(x, y);
              ++count;
            }
          }
        }
        if (count > 0u) {
          weightedSum += kWeights[ring] * (sum / static_cast<double>(count));
          weightTotal += kWeights[ring];
        }
      }
      return weightTotal > 0.0 ? weightedSum / weightTotal : mean(luminance);
    }
    case AutoExposureMethod::HighlightWeighted: {
      double weighted = 0.0;
      double total = 0.0;
      for (double value : luminance) {
        weighted += value * value * value;
        total += value * value;
      }
      return total < 1.0e-12 ? mean(luminance) : weighted / total;
    }
    case AutoExposureMethod::CenterWeighted:
    default: {
      constexpr double kSigma = 0.2;
      double weightedSum = 0.0;
      double weightSum = 0.0;
      for (int32_t y = 0; y < height; ++y) {
        for (int32_t x = 0; x < width; ++x) {
          const double weight = std::exp(-(xs[static_cast<size_t>(x)] * xs[static_cast<size_t>(x)] +
                                            ys[static_cast<size_t>(y)] * ys[static_cast<size_t>(y)]) /
                                          (2.0 * kSigma * kSigma));
          weightedSum += at(x, y) * weight;
          weightSum += weight;
        }
      }
      return weightedSum / std::max(weightSum, 1.0e-30);
    }
  }
}

// Exposure compensation in EV: -log2(metered / 0.184); 0 when the meter reads nothing usable.
inline float exposureCompensationEv(double metered) {
  const double exposure = metered / kMidGray;
  if (!(exposure > 0.0) || !std::isfinite(exposure)) {
    return 0.0f;
  }
  const double ev = -std::log2(exposure);
  return std::isfinite(ev) ? static_cast<float>(ev) : 0.0f;
}

}  // namespace spektrafilm::autoexposure
