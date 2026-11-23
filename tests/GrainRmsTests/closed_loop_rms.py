"""Closed-loop grain RMS: the RMS granularity measured in a rendered negative against the RMS model's target.

The RMS grain model (SpektraGrainRms.h; origin in its header) solves particle areas so that the
density fluctuation at net density 1.0 above base, seen through a 48 um circular aperture, equals the target (in
thousandths of density). This test renders flat neutral patches through the still renderer's diagnostic stage taps
(film-density with grain, film-density-pre-grain without), averages the density over 8x8 px blocks whose area equals
the 48 um aperture (film pixel 5.317 um: a 6770 px long edge of a 35 mm frame), takes the standard deviation of the
block means per patch and channel, and interpolates it at net density 1.0 of that channel.

Three regimes per case, each for film model 2 (checked against the tolerance) and film model 1 (reported):
- "single layer" (emulsion sublayers off; grain final blur, dye-cloud blur and microstructure off);
- "sublayers" (the renderer default, same blurs off);
- "look defaults" (sublayers and the renderer's blur defaults).
Measured 2026-11-23: model 2 1.00-1.04 in all three. Model 1 (unchanged for saved looks): single layer 1.02-1.04,
sublayers 0.80-1.01 (developed particle count capped at the expected seed count), look defaults 0.65-0.82 (final grain
blur not in the area solve). See tests/ParityTests/README.md.

Usage: python closed_loop_rms.py <SpektraStillRenderer.exe> [--tolerance 0.05] [--json out.json]
Exit code 1 when a film model 2 case misses its target by more than the tolerance.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import Worker  # noqa: E402

BLOCK = 8                                            # px per side of one aperture cell
PIXEL_UM = math.sqrt(math.pi) * 24.0 / BLOCK         # equal-area square of the 48 um circular aperture
WIDTH = round(36000.0 / PIXEL_UM)                    # 35 mm frame long edge -> 6770 px
HEIGHT = 400
LEVELS = [0.18 * 2.0 ** k for k in np.arange(-1.5, 3.01, 0.5)]  # linear Rec.709 neutral patch values
SEEDS = (1, 2, 3)

BASE = {"input-color": "linear_rec709", "output-color": "srgb", "spectral-method": "hanatos2025",
        "auto-exposure": "false", "film-exposure": "0", "film-format": "35", "mode": "scan",
        "halation-amount": "0", "scatter-amount": "0", "camera-diffusion-family": "off", "dir-amount": "0",
        "grain-model": "rms", "grain-rms-stock": "false"}
MODEL_ONLY = {"grain-final-blur": "0", "grain-dye-cloud-blur": "0", "grain-microstructure": "0"}
REGIMES = [  # name, options, checked against the tolerance
    (f"m{model} {name}", dict(options, **{"film-model": model}), model == "2")
    for model in ("2", "1")
    for name, options in (("single layer", dict(MODEL_ONLY, **{"grain-sublayers": "false"})),
                          ("sublayers", dict(MODEL_ONLY, **{"grain-sublayers": "true"})),
                          ("look defaults", {"grain-sublayers": "true"}))
]
CASES = [
    ("kodak_portra_400", (6.0, 6.0, 6.0)),
    ("kodak_portra_400", (12.0, 10.0, 8.0)),
    ("kodak_vision3_500t", (10.0, 10.0, 10.0)),
    ("kodak_ektar_100", (5.0, 5.0, 5.0)),
    ("fujifilm_c200", (9.0, 9.0, 9.0)),
]


def patches() -> np.ndarray:
    img = np.ones((HEIGHT, WIDTH, 4), np.float32)
    step = WIDTH // len(LEVELS)
    for i, level in enumerate(LEVELS):
        img[:, i * step:(i + 1) * step, :3] = level
    return img


def blocks(region: np.ndarray) -> np.ndarray:
    """Mean of each BLOCK x BLOCK cell (whole cells only), per channel."""
    h, w = (region.shape[0] // BLOCK) * BLOCK, (region.shape[1] // BLOCK) * BLOCK
    r = region[:h, :w].reshape(h // BLOCK, BLOCK, w // BLOCK, BLOCK, -1)
    return r.mean(axis=(1, 3)).reshape(-1, region.shape[-1])


def measure(worker: Worker, opts: dict, img: np.ndarray) -> "tuple[np.ndarray, np.ndarray]":
    """Per patch and channel: mean net density (grain-free) and RMS x1000 of the aperture-averaged grain."""
    clean = worker.render(img, dict(opts, **{"debug-tap": "film-density-pre-grain"}))[..., :3].astype(np.float64)
    step = WIDTH // len(LEVELS)
    margin = 2 * BLOCK  # stay clear of the patch edges, where the grain blur mixes two densities
    density = np.array([clean[:, i * step + margin:(i + 1) * step - margin].mean(axis=(0, 1))
                        for i in range(len(LEVELS))])
    rms = np.zeros((len(SEEDS), len(LEVELS), 3))
    for s, seed in enumerate(SEEDS):
        grain = worker.render(img, dict(opts, **{"debug-tap": "film-density", "grain-seed": str(seed)}))[..., :3]
        delta = grain.astype(np.float64) - clean
        for i in range(len(LEVELS)):
            cells = blocks(delta[margin:-margin, i * step + margin:(i + 1) * step - margin])
            rms[s, i] = cells.std(axis=0, ddof=1) * 1000.0
    return density, rms.mean(axis=0)


def at_density_one(density: np.ndarray, rms: np.ndarray) -> "list[float | None]":
    out = []
    for ch in range(3):
        d, r = density[:, ch], rms[:, ch]
        order = np.argsort(d)
        d, r = d[order], r[order]
        out.append(float(np.interp(1.0, d, r)) if d[0] <= 1.0 <= d[-1] else None)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("exe")
    ap.add_argument("--tolerance", type=float, default=0.05)
    ap.add_argument("--json")
    a = ap.parse_args()
    img = patches()
    worker = Worker(a.exe)
    report, failures = [], 0
    try:
        for stock, target in CASES:
            targets = {f"grain-rms-{ch}": str(v) for ch, v in zip("rgb", target)}
            for regime, extra, checked in REGIMES:
                opts = dict(BASE, stock=stock, **targets, **extra)
                density, rms = measure(worker, opts, img)
                measured = at_density_one(density, rms)
                ratios = [None if m is None else m / t for m, t in zip(measured, target)]
                bad = checked and any(r is None or abs(r - 1.0) > a.tolerance for r in ratios)
                failures += bad
                report.append({"stock": stock, "target": target, "regime": regime, "measured": measured,
                               "ratio": ratios, "density": density.tolist(), "rms": rms.tolist()})
                shown = " ".join("  n/a " if r is None else f"{r:6.3f}" for r in ratios)
                print(f"{'FAIL' if bad else ('ok  ' if checked else 'info')} {stock:22s} target {target} "
                      f"{regime:16s} measured/target R G B: {shown}")
    finally:
        worker.close()
    if a.json:
        Path(a.json).write_text(json.dumps(report, indent=2))
    print(("PASS" if not failures else f"FAIL {failures}") + f" (tolerance {a.tolerance:.0%}, aperture 48 um = "
          f"{BLOCK}x{BLOCK} px at {PIXEL_UM:.3f} um)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
