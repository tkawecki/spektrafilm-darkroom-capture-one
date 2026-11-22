"""CPU check of the Grain synthesis size matching for the transmission-domain final blur.

With the final blur in transmission, every step after the per-pixel coverage estimate is linear in coverage:
the Gaussian observation window, the discrete final blur and box downscaling. The variance of a downscaled
reference render (6 um pixels) and of a native render at a larger pixel P can therefore be written as sums of
window-to-window covariances of the Boolean grain model, weighted by the autocorrelation of the blur and box
kernels. This script implements those formulas exactly as the renderer and shader do and checks them against a
Monte Carlo simulation of a disc Boolean model on a fine raster. CPU only.

Usage: python transmission_variance.py [--realizations N]
"""
import argparse
import math
import numpy as np
from scipy import ndimage, special

LN10 = math.log(10.0)
REFERENCE_PIXEL_UM = 6.0
LAG_CLASSES = [(m, n) for m in range(4) for n in range(m + 1)]  # 10 classes, m >= n >= 0
GH_NODES = np.array([-2.856970014, -1.355626180, 0.0, 1.355626180, 2.856970014])
GH_WEIGHTS = np.array([0.011257411, 0.222075922, 0.533333333, 0.222075922, 0.011257411])


def radii_nodes(rbar, ratio, rmax):
    s = math.sqrt(math.log(1 + ratio * ratio))
    mu = math.log(rbar) - 0.5 * s * s
    return np.minimum(np.exp(mu + s * GH_NODES), rmax)


def disc_overlap(h, r):
    h = np.asarray(h, float)
    x = np.clip(h / (2 * r), 0, 1)
    return np.where(h < 2 * r, 2 * r * r * (np.arccos(x) - x * np.sqrt(np.maximum(1 - x * x, 0))), 0.0)


def window_covariance(density, sigma, d, rbar, ratio, rmax, steps=32):
    """Covariance of two Gaussian-window coverage averages (window sigma) whose centres are d apart."""
    radii = radii_nodes(rbar, ratio, rmax)
    mean_area = math.pi * rbar * rbar * (1 + ratio * ratio)
    lam = density * LN10 / mean_area * mean_area / float(np.sum(GH_WEIGHTS * math.pi * radii ** 2))
    q2 = math.exp(-2 * density * LN10)
    dh = 2 * radii.max() / steps
    h = (np.arange(steps) + 0.5) * dh
    overlap = sum(w * disc_overlap(h, r) for w, r in zip(GH_WEIGHTS, radii))
    cov = q2 * np.expm1(lam * overlap)
    s2 = 2 * sigma * sigma  # window autocorrelation variance per axis
    ring = np.exp(-(h - d) ** 2 / (2 * s2)) * special.i0e(h * d / s2) / (2 * math.pi * s2)
    return float(np.sum(cov * ring * 2 * math.pi * h) * dh)


def gaussian_kernel(sigma_px, sublayers=True):
    if sigma_px <= 0 or (not sublayers and sigma_px <= 0.4):
        return np.array([1.0])
    radius = min(int(math.ceil(3 * sigma_px)), 64)
    o = np.arange(-radius, radius + 1)
    w = np.exp(-0.5 * (o / sigma_px) ** 2)
    return w / w.sum()


def box_kernel(k, start):
    """Weights of reference pixels (pixel i covers [i - 0.5, i + 0.5)) inside the box [start, start + k)."""
    i = np.arange(int(math.floor(start)) - 1, int(math.ceil(start + k)) + 2)
    w = np.clip(np.minimum(i + 0.5, start + k) - np.maximum(i - 0.5, start), 0, None)
    return w[w > 0] / k


BOX_PHASES = 8


def autocorrelation(kernel, lag):
    if lag >= len(kernel):
        return 0.0
    return float(np.dot(kernel[: len(kernel) - lag], kernel[lag:]))


def lag_weights(kernel):
    """Weight of each lag class (m, n): sum over all 2-D lags with |lag_x|, |lag_y| in that class."""
    weights = []
    for m, n in LAG_CLASSES:
        mult = (1 if m == 0 else 2) * (1 if n == 0 else 2) * (1 if m == n else 2)
        weights.append(mult * autocorrelation(kernel, m) * autocorrelation(kernel, n))
    return np.array(weights)


def reference_weights(blur_um, pixel_um, sublayers=True, aligned=False):
    """Lag weights of the reference blur followed by box downscaling to pixel_um. The renderer averages the
    box over BOX_PHASES sub-pixel positions (a generic, unaligned downscale); aligned=True uses whole blocks."""
    k = pixel_um / REFERENCE_PIXEL_UM
    g = gaussian_kernel(blur_um / REFERENCE_PIXEL_UM, sublayers)
    # The phase spread grows from none at k = 1 (identity) to a whole pixel at k >= 2, so matching is continuous
    # at the reference pixel.
    spread = min(max(k - 1.0, 0.0), 1.0)
    starts = [-0.5] if aligned else [-0.5 + spread * (j + 0.5) / BOX_PHASES for j in range(BOX_PHASES)]
    return np.mean([lag_weights(np.convolve(g, box_kernel(k, s))) for s in starts], axis=0)


def native_weights(blur_um, pixel_um, sublayers=True):
    return lag_weights(gaussian_kernel(blur_um / pixel_um, sublayers))


def lag_distances(spacing):
    return np.array([spacing * math.hypot(m, n) for m, n in LAG_CLASSES])


def predicted_variance(weights, density, sigma, spacing, layer):
    return sum(w * window_covariance(density, sigma, d, *layer)
               for w, d in zip(weights, lag_distances(spacing)) if w != 0)


# ---------------------------------------------------------------- Monte Carlo on a fine raster
def boolean_field(size_um, step_um, density, rbar, ratio, rmax, rng, classes=8):
    """Disc Boolean model on a periodic raster; radii are grouped into classes, each drawn by a distance transform."""
    n = int(size_um / step_um)
    mean_area = math.pi * rbar * rbar * (1 + ratio * ratio)
    count = rng.poisson(density * LN10 / mean_area * size_um * size_um)
    cy, cx = rng.integers(0, n, count), rng.integers(0, n, count)
    if ratio > 0:
        s = math.sqrt(math.log(1 + ratio * ratio))
        r = np.minimum(np.exp(math.log(rbar) - 0.5 * s * s + s * rng.standard_normal(count)), rmax)
        edges = np.quantile(r, np.linspace(0, 1, classes + 1))
        group = np.clip(np.searchsorted(edges, r, side="right") - 1, 0, classes - 1)
    else:
        r, group = np.full(count, rbar), np.zeros(count, int)
    covered = np.zeros((n, n), bool)
    for g in np.unique(group):
        sel = group == g
        radius = float(np.sqrt(np.mean(r[sel] ** 2)))  # keeps the class's mean area
        seeds = np.ones((3 * n, 3 * n), bool)  # 3x3 tiling makes the distance transform periodic
        for dy in (0, n, 2 * n):
            for dx in (0, n, 2 * n):
                seeds[cy[sel] + dy, cx[sel] + dx] = False
        dist = ndimage.distance_transform_edt(seeds)[n:2 * n, n:2 * n] * step_um
        covered |= dist <= radius
    return covered.astype(np.float64)


def pipelines(field, step_um, pixel_um, sigma_native, blur_um):
    """Variance of the downscaled reference render and of the native render at pixel_um."""
    k = int(round(pixel_um / REFERENCE_PIXEL_UM))
    win = ndimage.gaussian_filter(field, 1.0 / step_um, mode="wrap")
    stride = int(round(REFERENCE_PIXEL_UM / step_um))
    ref = win[stride // 2::stride, stride // 2::stride]
    g = gaussian_kernel(blur_um / REFERENCE_PIXEL_UM)
    ref = ndimage.convolve1d(ndimage.convolve1d(ref, g, axis=0, mode="wrap"), g, axis=1, mode="wrap")
    h = ref.shape[0] // k * k
    ref = ref[:h, :h].reshape(h // k, k, h // k, k).mean(axis=(1, 3))
    winn = ndimage.gaussian_filter(field, sigma_native / step_um, mode="wrap")
    strn = int(round(pixel_um / step_um))
    nat = winn[strn // 2::strn, strn // 2::strn]
    gn = gaussian_kernel(blur_um / pixel_um)
    nat = ndimage.convolve1d(ndimage.convolve1d(nat, gn, axis=0, mode="wrap"), gn, axis=1, mode="wrap")
    return ref.ravel(), nat.ravel()


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--realizations", type=int, default=4)
    a = a.parse_args()
    rng = np.random.default_rng(11)
    blur, step, size = 7.17, 0.2, 1152.0
    pixels = (12.0, 24.0, 48.0)
    cases = [
        ("G coarse, fixed radius", 0.5, (1.5, 0.0, 1.5)),
        ("B coarse, fixed radius", 0.4, (3.75, 0.0, 3.75)),
        ("G coarse, radius variation 0.3", 0.5, (1.5, 0.3, 1.5 * 1.9)),
    ]
    ok = True
    for name, density, layer in cases:
        samples = {p: ([], []) for p in pixels}
        for _ in range(a.realizations):
            field = boolean_field(size, step, density, *layer, rng)
            for p in pixels:
                ref, nat = pipelines(field, step, p, max(1.0, p / 6.0), blur)
                samples[p][0].append(ref)
                samples[p][1].append(nat)
        for p in pixels:
            sigma_native = max(1.0, p / 6.0)
            ref_sim = float(np.var(np.concatenate(samples[p][0])))
            nat_sim = float(np.var(np.concatenate(samples[p][1])))
            ref_pred = predicted_variance(reference_weights(blur, p, aligned=True), density, 1.0, REFERENCE_PIXEL_UM, layer)
            nat_pred = predicted_variance(native_weights(blur, p), density, sigma_native, p, layer)
            e1, e2 = ref_pred / ref_sim - 1, nat_pred / nat_sim - 1
            ok &= abs(e1) < 0.15 and abs(e2) < 0.15
            print(f"{name:31s} P={p:3.0f} um  reference sim {ref_sim:.3e} formula {ref_pred:.3e} ({e1:+.1%})"
                  f"  native sim {nat_sim:.3e} formula {nat_pred:.3e} ({e2:+.1%})  scale {math.sqrt(ref_pred / nat_pred):.3f}",
                  flush=True)
    print("PASS" if ok else "FAIL")
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
