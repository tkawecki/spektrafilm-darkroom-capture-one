"""Grain checks for film model 2 (exact Poisson counts for few developed particles).

1. Model 1 with grain is bit-identical between the installed renderer and the candidate.
2. Mean shift of grain on flat grey patches (grained minus grain-free mean, in output code values x 65535),
   models 1 and 2, at a Viewer-like and an export-like pixel size. Model 2 should be closer to zero where few
   particles develop (thin negative areas = dark scene greys on a negative).

Usage: python film_model_grain.py <reference renderer.exe> <candidate renderer.exe> <args.json>
Each renderer needs its own shaders/ folder next to it; the candidate must carry the SpektraGrain.comp.spv built
from this repository (glslang -V). args.json is a flat renderer argument list saved from the panel.
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_renderers import Worker  # noqa: E402

OLD, NEW, args_path = sys.argv[1], sys.argv[2], sys.argv[3]
flat = json.loads(open(args_path).read())
base = {flat[i][2:]: flat[i + 1] for i in range(0, len(flat), 2)}
base.update({"halation-amount": "0", "scatter-amount": "0", "camera-diffusion-family": "off", "print-diffusion-family": "off",
             "dir-diffusion": "0", "scanner-enabled": "false", "auto-exposure": "false", "film-exposure": "0",
             "input-color": "prophoto", "output-color": "srgb", "grain-model": "production", "grain-mean-restore": "false"})

LEVELS = [0.004, 0.01, 0.02, 0.05, 0.1, 0.2, 0.4, 0.8]


def patches(width: int, height: int) -> np.ndarray:
    image = np.ones((height, width, 4), np.float32)
    step = width // len(LEVELS)
    for i, level in enumerate(LEVELS):
        image[:, i * step:(i + 1) * step, :3] = level
    return image


def means(img: np.ndarray, width: int) -> np.ndarray:
    step = width // len(LEVELS)
    return np.array([img[:, i * step + step // 8:(i + 1) * step - step // 8, :3].mean() for i in range(len(LEVELS))])


old, new = Worker(OLD), Worker(NEW)
try:
    # 1. model 1 identity with grain, two grain models
    small = patches(1600, 200)
    for model in ("production", "preview"):
        o = dict(base, **{"grain-model": model})
        a, b = old.render(small, o), new.render(small, o)
        print(f"model 1 identity ({model} grain): max diff {float(np.abs(a - b).max()):.3g}")
    # 2. mean shift; the long edge sets the film pixel size (35 mm frame)
    for width, label in ((1600, "Viewer-like 1600 px (21.9 um)"), (8000, "export-like 8000 px (4.4 um)")):
        img = patches(width, 256)
        clean = means(new.render(img, dict(base, **{"grain-model": "off"})), width)
        row = {}
        for model in ("1", "2"):
            o = dict(base)
            if model == "2":
                o["film-model"] = "2"
            shifts = []
            for seed in (1, 2, 3):
                shifts.append(means(new.render(img, dict(o, **{"grain-seed": str(seed)})), width) - clean)
            row[model] = np.mean(shifts, axis=0) * 65535
        print(label)
        print("  scene grey   " + " ".join(f"{v:>7}" for v in LEVELS))
        for model in ("1", "2"):
            print(f"  model {model} shift " + " ".join(f"{v:7.1f}" for v in row[model]))
finally:
    old.close()
    new.close()
