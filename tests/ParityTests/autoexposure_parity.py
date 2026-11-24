"""Auto exposure of the Vulkan still renderer (film model 4) against the Python reference (spektrafilm).

Python meters with spektrafilm.utils.autoexposure.measure_autoexposure_ev on ResizingService.small_preview (skimage
rescale to a 256 px long edge, order 0 with anti-aliasing). The renderer's metered EV is read from its film-log tap:
with spatial effects off, log10 film exposure moves by EV * log10(2) between auto exposure on and off.

Usage (Python with the reference's dependencies, e.g. artifacts/quality-audit-20261115/venv):
  python autoexposure_parity.py <SpektraStillRenderer.exe> <upstream-spektrafilm/src> [--model 4] [--tolerance 0.002]
Exit code 1 when any method differs from Python by more than the tolerance (EV) at film model 4.
"""
import argparse
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import Worker  # noqa: E402

METHODS = ["center_weighted", "average", "median", "partial", "matrix", "multi_zone", "highlight_weighted"]
OPTIONS = {
    "stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "prophoto",
    "output-color": "srgb", "spectral-method": "hanatos2026", "color-adaptation": "true", "film-exposure": "0",
    "print-exposure": "0", "film-format": "35", "grain-model": "off", "halation-amount": "0", "scatter-amount": "0",
    "camera-diffusion-family": "off", "print-diffusion-family": "off", "dir-amount": "0", "dir-diffusion": "0",
    "scanner-enabled": "false", "debug-tap": "film-log",
}


def romm_encode(linear: np.ndarray) -> np.ndarray:
    return np.where(linear < 1 / 512, linear * 16, np.power(np.clip(linear, 0, None), 1 / 1.8))


def scene(width: int, height: int, seed: int = 5) -> np.ndarray:
    """A ROMM-encoded ProPhoto scene with an off-centre highlight, a dark corner and fine texture, so the methods
    meter differently and the anti-aliasing filter matters."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width].astype(np.float64)
    u, v = x / width, y / height
    linear = np.empty((height, width, 3))
    base = 0.08 + 0.12 * u + 0.05 * np.sin(x / 23.0) * np.cos(y / 31.0)
    linear[..., 0] = base * (1.0 + 0.3 * v)
    linear[..., 1] = base
    linear[..., 2] = base * (1.2 - 0.4 * u)
    highlight = np.exp(-(((u - 0.72) / 0.08) ** 2 + ((v - 0.3) / 0.1) ** 2))
    # Peak below 2.0 linear: ROMM-encoded under 1.5, the top of the renderer's decoding table (beyond it the table
    # extrapolates linearly, Python keeps the power curve).
    linear += 1.6 * highlight[..., None] * np.array([1.0, 0.95, 0.85])
    linear[(u < 0.2) & (v > 0.75)] *= 0.05
    stripes = ((x.astype(np.int64) // 3) % 2 == 0) & (u > 0.4) & (u < 0.55)
    linear[stripes] *= 3.0
    linear *= np.exp(np.clip(rng.normal(0.0, 0.05, (height, width, 1)), -0.1, 0.1))
    image = np.ones((height, width, 4), np.float32)
    image[..., :3] = romm_encode(linear)
    return image


def python_ev(image: np.ndarray, method: str) -> float:
    from spektrafilm.runtime.services.resize import ResizingService
    from spektrafilm.utils.autoexposure import measure_autoexposure_ev
    preview = ResizingService.small_preview(None, image[..., :3].astype(np.float64))
    return float(measure_autoexposure_ev(preview, "ProPhoto RGB", True, method=method))


def renderer_ev(worker: Worker, image: np.ndarray, method: str, model: str) -> float:
    options = dict(OPTIONS, **{"film-model": model, "auto-exposure-meter": method})
    on = worker.render(image, dict(options, **{"auto-exposure": "true"}))[..., :3]
    off = worker.render(image, dict(options, **{"auto-exposure": "false"}))[..., :3]
    usable = off > -5.0
    return float(np.median((on - off)[usable]) / math.log10(2.0))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("exe")
    parser.add_argument("reference_src")
    parser.add_argument("--model", default="4")
    parser.add_argument("--tolerance", type=float, default=0.002, help="largest allowed EV difference")
    args = parser.parse_args()
    sys.path.insert(0, args.reference_src)
    worker = Worker(args.exe)
    failures = 0
    try:
        for width, height in ((3000, 2000), (1997, 3001), (200, 150)):
            image = scene(width, height)
            print(f"{width} x {height}")
            for method in METHODS:
                reference = python_ev(image, method)
                ours = renderer_ev(worker, image, method, args.model)
                difference = ours - reference
                failed = abs(difference) > args.tolerance
                failures += failed
                print(f"  {method:20s} Python {reference:+.4f} EV  renderer {ours:+.4f} EV  "
                      f"difference {difference:+.4f}{'  FAIL' if failed else ''}")
    finally:
        worker.close()
    return 1 if failures and args.model == "4" else 0


if __name__ == "__main__":
    sys.exit(main())
