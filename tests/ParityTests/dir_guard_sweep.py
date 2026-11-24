"""DIR curve guard (film model 4): with strong DIR settings film density must still follow exposure monotonically.

Every film of the renderer develops a grey exposure ramp (spatial effects off, DIR diffusion off) at strong DIR
settings; the film-density tap must rise with exposure for negatives and fall for slides. Reported per film and
setting: the number of ramp steps that go the wrong way by more than 0.002 density, and the largest such step.
Film model 4 must have none; the same sweep at film model 3 shows where the curves folded before the guard.

Usage:
  python dir_guard_sweep.py <SpektraStillRenderer.exe> [--model 4] [--films kodak_portra_400 ...]
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "upstream-ofx/tools"))
from compare_renderers import Worker  # noqa: E402

BASE = {
    "paper": "kodak_portra_endura", "input-color": "prophoto", "output-color": "srgb",
    "spectral-method": "hanatos2026", "color-adaptation": "true", "auto-exposure": "false", "print-exposure": "0",
    "film-format": "35", "grain-model": "off", "halation-amount": "0", "scatter-amount": "0",
    "camera-diffusion-family": "off", "print-diffusion-family": "off", "dir-diffusion": "0", "dir-tail-weight": "0",
    "scanner-enabled": "false", "debug-tap": "film-density",
}
SETTINGS = {
    "DIR off": {"dir-amount": "0"},
    "amount 1, inhibition 2": {"dir-amount": "1", "dir-same-layer": "2", "dir-interlayer": "2"},
    "amount 2, inhibition 1": {"dir-amount": "2", "dir-same-layer": "1", "dir-interlayer": "1"},
    "amount 2, inhibition 2": {"dir-amount": "2", "dir-same-layer": "2", "dir-interlayer": "2"},
}
TOLERANCE = 0.002


def ramp(width: int = 2048) -> np.ndarray:
    """ROMM-encoded grey from -10 EV to +3 EV around mid grey; rendered at two film exposures to cover the curve."""
    linear = 0.184 * np.exp2(np.linspace(-10.0, 3.0, width))
    encoded = np.where(linear < 1 / 512, linear * 16, linear ** (1 / 1.8))
    image = np.ones((4, width, 4), np.float32)
    image[..., :3] = encoded[None, :, None]
    return image


def stock_type(stock: str) -> str:
    """The film type from the profile the renderer's tables were generated from (model 3 and later)."""
    root = Path(__file__).resolve().parents[2]
    for folder in (root / "upstream-spektrafilm/src/spektrafilm/data/profiles", root / "upstream-ofx/Resources/data/profiles"):
        path = folder / f"{stock}.json"
        if path.exists():
            return json.loads(path.read_text(encoding="utf-8")).get("info", {}).get("type", "negative")
    return "negative"


def wrong_way(density: np.ndarray, positive: bool) -> tuple[int, float]:
    steps = np.diff(density, axis=0)
    if positive:
        steps = -steps
    bad = steps < -TOLERANCE
    return int(bad.sum()), float(-steps[bad].min()) if bad.any() else 0.0


def main() -> int:
    from ofx_stock_lists import FILMS
    parser = argparse.ArgumentParser()
    parser.add_argument("exe")
    parser.add_argument("--model", default="4")
    parser.add_argument("--films", nargs="*")
    args = parser.parse_args()
    films = args.films or sorted(FILMS)
    image = ramp()
    worker = Worker(args.exe)
    folded = 0
    try:
        for stock in films:
            positive = stock_type(stock) == "positive"
            mode = "scan" if positive else "print"
            line = []
            for label, setting in SETTINGS.items():
                runs = []
                for exposure in ("0", "4"):
                    options = dict(BASE, stock=stock, mode=mode, **setting,
                                   **{"film-model": args.model, "film-exposure": exposure})
                    runs.append(worker.render(image, options)[1, :, :3])
                count = 0
                largest = 0.0
                for run in runs:
                    c, m = wrong_way(run, positive)
                    count += c
                    largest = max(largest, m)
                folded += count > 0
                line.append(f"{label}: {count:4d} steps{f' (max {largest:.3f})' if count else ''}")
            print(f"{stock:32s} " + " | ".join(line))
    finally:
        worker.close()
    print(f"settings with folded curves: {folded}")
    return 1 if folded and args.model == "4" else 0


if __name__ == "__main__":
    sys.exit(main())
