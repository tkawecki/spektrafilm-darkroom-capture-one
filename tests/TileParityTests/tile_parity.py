"""Tile-parity check: the same frame must not depend on the renderer's tile layout.

Renders one float RGBA frame through SpektraStillRenderer with several tile and
stream configurations and reports the largest difference against a reference
(largest tile). Diagnostic environment variables are passed straight to the
renderer process (the panel strips them on purpose).

Usage:
  python tile_parity.py --exe RENDERER --source frame.f32 --size 8514x5676 \
      --args args.json [--set key=value ...] [--config NAME:MODE:ENV=V,ENV=V ...]
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import Worker  # noqa: E402

DEFAULT_CONFIGS = [
    "reference:--export-stream:SPEKTRAFILM_VULKAN_TILE_WIDTH=8192,SPEKTRAFILM_VULKAN_TILE_HEIGHT=8192",
    "export-default:--export-stream:",
    "preview-default:--native-preview-stream:SPEKTRAFILM_VULKAN_TILE_WIDTH=2048,SPEKTRAFILM_VULKAN_TILE_HEIGHT=2048",
]


def parse_config(text: str) -> tuple[str, str, dict[str, str]]:
    name, mode, env_text = text.split(":", 2)
    env = dict(item.split("=", 1) for item in env_text.split(",") if item)
    return name, mode, env


def options_from(args_path: Path, overrides: list[str]) -> dict[str, str]:
    flat = json.loads(args_path.read_text())
    options = {flat[i][2:]: flat[i + 1] for i in range(0, len(flat), 2)}
    for item in overrides:
        key, value = item.split("=", 1)
        options[key] = value
    return options


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--size", required=True)
    parser.add_argument("--args", required=True, type=Path)
    parser.add_argument("--set", action="append", default=[])
    parser.add_argument("--config", action="append", default=[])
    parser.add_argument("--json", type=Path)
    a = parser.parse_args()
    width, height = map(int, a.size.split("x"))
    image = np.fromfile(a.source, "<f4").reshape(height, width, 4)
    options = options_from(a.args, a.set)
    reference = None
    report = []
    for text in a.config or DEFAULT_CONFIGS:
        name, mode, env = parse_config(text)
        worker = Worker(a.exe, mode, env)
        try:
            out = worker.render(image, options)[..., :3].astype(np.float64)
        finally:
            worker.close()
        if reference is None:
            reference = out
            print(f"{name}: reference", flush=True)
            continue
        diff = np.abs(out - reference)
        index = np.unravel_index(int(np.argmax(diff)), diff.shape)
        row = {"config": name, "max": float(diff.max()), "mean": float(diff.mean()),
               "over_1e6": int((diff > 1e-6).sum()), "worst_xy": [int(index[1]), int(index[0])]}
        report.append(row)
        print(f"{name}: max {row['max']:.3e} at {row['worst_xy']}, mean {row['mean']:.3e}, "
              f"{row['over_1e6']} values > 1e-6", flush=True)
    if a.json:
        a.json.write_text(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
