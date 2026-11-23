"""Neutral print filters for film model 3, fitted with the still renderer (Spektrafilm C1, 2026-11-23).

Project decision: the neutral print filters are computed by this renderer
for each film/paper pair and film model version, not copied from upstream tables. For every negative film and paper
the magenta and yellow filters (Kodak CC) are adjusted until a scene mid grey (0.184, linear) prints neutral: equal
linear R, G and B in the output, with the panel's defaults (Hanatos 2026, colour adaptation on, no exposure offsets,
TH-KG3 enlarger). The cyan filter stays as in the starting table. Slide films are scanned directly and keep the
starting values.

Usage: python fit_neutral_print_filters.py <SpektraStillRenderer.exe> <start.json> <output.json> --profiles <dir> [--model 3]
The renderer must have been generated with <start.json> as its model 3 table (generate_profile_curves.py
--model3-neutral-filters), because its --magenta-filter/--yellow-filter options shift that table.
The output has the structure of neutral_print_filters.json; profiles and therefore these filters derive from
spektrafilm by Andrea Volpato (CC BY-SA 4.0).
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "GrainMeanTests"))
from ofx_stock_lists import FILMS, PAPERS  # noqa: E402
from compare_renderers import Worker  # noqa: E402

GREY = 0.184
BASE = {"input-color": "linear_rec709", "output-color": "linear_rec709", "spectral-method": "hanatos2026",
        "color-adaptation": "true", "auto-exposure": "false", "film-exposure": "0", "print-exposure": "0",
        "mode": "print", "grain-model": "off", "halation-amount": "0", "scatter-amount": "0",
        "camera-diffusion-family": "off", "print-diffusion-family": "off", "scanner-enabled": "false", "dir-amount": "0"}


def imbalance(worker: Worker, opts: dict, magenta: float, yellow: float) -> np.ndarray:
    image = np.ones((8, 8, 4), np.float32)
    image[..., :3] = GREY
    rgb = worker.render(image, dict(opts, **{"magenta-filter": f"{magenta:.6f}", "yellow-filter": f"{yellow:.6f}"}))
    r, g, b = (float(v) for v in rgb[..., :3].reshape(-1, 3).mean(axis=0))
    return np.array([np.log(r / g), np.log(b / g)])


def fit(worker: Worker, opts: dict, tolerance: float = 2e-5, step: float = 0.5) -> "tuple[float, float, float, int]":
    x = np.zeros(2)
    f = imbalance(worker, opts, *x)
    for iteration in range(12):
        if np.abs(f).max() < tolerance:
            return float(x[0]), float(x[1]), float(np.abs(f).max()), iteration
        jacobian = np.column_stack([(imbalance(worker, opts, *(x + step * e)) - f) / step for e in np.eye(2)])
        x = x - np.linalg.solve(jacobian, f)
        f = imbalance(worker, opts, *x)
    raise RuntimeError(f"no convergence for {opts['stock']} on {opts['paper']}: {f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("exe")
    ap.add_argument("start", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument("--model", default="3")
    ap.add_argument("--profiles", type=Path, required=True, help="the profiles the model's tables were generated from")
    a = ap.parse_args()
    start = json.loads(a.start.read_text(encoding="utf-8"))
    out = {"_about": "Neutral print filters (Kodak CC, TH-KG3) of film model " + a.model + ", fitted with "
                     "SpektraStillRenderer by upstream-ofx/tools/fit_neutral_print_filters.py: a scene mid grey prints "
                     "neutral. Derived from spektrafilm profiles by Andrea Volpato, "
                     "https://github.com/andreavolpato/spektrafilm, CC BY-SA 4.0; modified by Spektrafilm C1."}
    worker = Worker(a.exe)
    worst = 0.0
    try:
        for paper in PAPERS:
            table = {}
            for film in FILMS:
                c, m, y = start[paper]["TH-KG3"][film]
                if json.loads((a.profiles / f"{film}.json").read_text(encoding="utf-8"))["info"]["type"] == "positive":
                    table[film] = [c, m, y]  # slides are scanned, never printed
                    continue
                opts = dict(BASE, stock=film, paper=paper, **{"film-model": a.model})
                dm, dy, residual, iterations = fit(worker, opts)
                worst = max(worst, residual)
                if m + dm < 0.0 or y + dy < 0.0:
                    # The renderer clamps a filter at 0 CC, so a negative solution would not be the neutral one.
                    raise RuntimeError(f"negative filter for {film} on {paper}: M {m + dm:.2f}, Y {y + dy:.2f}")
                table[film] = [c, m + dm, y + dy]
                print(f"{paper:28s} {film:26s} M {m:7.2f} -> {m + dm:7.2f}  Y {y:7.2f} -> {y + dy:7.2f}  ({iterations} it)")
            out[paper] = {"TH-KG3": table}
    finally:
        worker.close()
    a.output.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {a.output}; worst remaining log imbalance {worst:.1e}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
