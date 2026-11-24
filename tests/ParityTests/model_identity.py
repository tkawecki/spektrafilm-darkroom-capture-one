"""Older film models must render bit for bit as before a new model is added.

Renders the same synthetic scene with a baseline renderer (the installed one) and a candidate across film models and
configurations that touch every model-dependent stage: auto exposure (each metering), DIR (default and strong),
output gamut compression (colour adaptation on), print and scan, grain, scanner, tiles. Reports, per model and
configuration, whether the pixels are identical; exit code 1 when any listed model differs.

Usage:
  python model_identity.py <baseline SpektraStillRenderer.exe> <candidate SpektraStillRenderer.exe> [--models 1 2 3]
                           [--size 1500x1000] [--tile 512]
"""
import argparse
import hashlib
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import BASE, Worker, scene  # noqa: E402

CONFIGS = {
    "grain off": {"grain-model": "off"},
    "production grain": {"grain-model": "production"},
    "rms grain": {"grain-model": "rms"},
    "colour adaptation": {"grain-model": "off", "color-adaptation": "true"},
    "colour adaptation, Rec.709 out": {"grain-model": "off", "color-adaptation": "true",
                                       "output-color": "rec709_gamma24"},
    "auto exposure median": {"grain-model": "off", "auto-exposure-meter": "median"},
    "auto exposure off": {"grain-model": "off", "auto-exposure": "false"},
    "DIR default": {"grain-model": "off", "dir-amount": "1", "dir-diffusion": "20"},
    "DIR strong": {"grain-model": "off", "dir-amount": "2", "dir-same-layer": "2", "dir-interlayer": "2",
                   "dir-diffusion": "20"},
    "halation, scatter": {"grain-model": "production", "halation-amount": "2", "scatter-amount": "1"},
    "scanner": {"grain-model": "production", "scanner-enabled": "true", "scanner-mtf": "60",
                "scanner-unsharp-radius": "5", "scanner-unsharp-amount": "0.7", "scanner-glare": "0.03"},
    "scanner, colour adaptation": {"grain-model": "production", "scanner-enabled": "true", "scanner-mtf": "60",
                                   "scanner-unsharp-radius": "5", "scanner-unsharp-amount": "0.7",
                                   "color-adaptation": "true"},
    "scan negative": {"grain-model": "off", "mode": "scan", "color-adaptation": "true"},
    "scan slide": {"grain-model": "off", "mode": "scan", "stock": "fujifilm_provia_100f", "color-adaptation": "true"},
}


def digest(pixels) -> str:
    return hashlib.sha256(pixels.tobytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline")
    parser.add_argument("candidate")
    parser.add_argument("--models", nargs="+", default=["1", "2", "3"])
    parser.add_argument("--size", default="1500x1000")
    parser.add_argument("--tile", type=int, default=0, help="also force tiles of this size (renderer environment)")
    args = parser.parse_args()
    width, height = map(int, args.size.split("x"))
    image = scene(width, height)
    env = {"SPEKTRAFILM_VULKAN_TILE_WIDTH": str(args.tile), "SPEKTRAFILM_VULKAN_TILE_HEIGHT": str(args.tile)} \
        if args.tile else {}
    baseline, candidate = Worker(args.baseline, env_extra=env), Worker(args.candidate, env_extra=env)
    differing = 0
    try:
        for model in args.models:
            for name, extra in CONFIGS.items():
                options = {**BASE, **extra}
                if model != "1":
                    options["film-model"] = model
                same = digest(baseline.render(image, options)) == digest(candidate.render(image, options))
                differing += not same
                print(f"model {model}  {name:34s} {'identical' if same else 'DIFFERENT'}", flush=True)
    finally:
        baseline.close()
        candidate.close()
    print(f"differing: {differing}")
    return 1 if differing else 0


if __name__ == "__main__":
    sys.exit(main())
