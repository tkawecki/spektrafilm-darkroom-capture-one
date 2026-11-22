"""Convert manual renders (PNG frames saved by the manual-shots hook command) to WebP in docs/manual/img.

The images are local only: docs/manual/img is ignored by Git, because they show the user's photos.
Usage: python -I docs/manual-en/tools/build_images.py <shots-result.json> [...] [--width 1600] [--crop x0,y0,x1,y1 --suffix crop]
Each result file is the hook's result.json copy; every rendered frame becomes img/<set>-<name>.webp.
Frames are first cut to the Capture One crop recorded by the hook ("crop", fractions of the frame), because the
Viewer frame holds the whole canvas. With --crop (fractions of that cropped image) a detail region is cut out next,
for 100% views of grain, halation and diffusion, and saved as img/<set>-<name>-<suffix>.webp.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image

MANUAL = Path(__file__).resolve().parent.parent
TARGET = MANUAL / "img"


def convert(result_file: Path, width: int, quality: int, crop: tuple[float, float, float, float] | None = None,
            suffix: str = "") -> int:
    data = json.loads(result_file.read_text(encoding="utf-8"))
    result = data.get("result", data)
    folder = Path(result["Folder"])
    count = 0
    for frame in result["rendered"]:
        source = folder / frame["file"]
        if not source.is_file():
            raise FileNotFoundError(f"Render missing: {source}")
        with Image.open(source) as image:
            image = image.convert("RGB")
            if frame.get("crop"):
                left, top, right, bottom = frame["crop"]
                image = image.crop((round(left * image.width), round(top * image.height),
                                    round(right * image.width), round(bottom * image.height)))
            if crop:
                x0, y0, x1, y1 = crop
                image = image.crop((round(x0 * image.width), round(y0 * image.height),
                                    round(x1 * image.width), round(y1 * image.height)))
            if image.width > width:
                image = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
            target = TARGET / (source.stem + (f"-{suffix}" if suffix else "") + ".webp")
            image.save(target, "WEBP", quality=quality, method=6)
        count += 1
    return count


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("results", nargs="+", type=Path)
    parser.add_argument("--width", type=int, default=1600)
    parser.add_argument("--quality", type=int, default=84)
    parser.add_argument("--crop", type=lambda text: tuple(float(v) for v in text.split(",")))
    parser.add_argument("--suffix", default="")
    args = parser.parse_args()
    if args.crop and (len(args.crop) != 4 or not args.suffix):
        parser.error("--crop needs four fractions and a --suffix")
    TARGET.mkdir(parents=True, exist_ok=True)
    total = sum(convert(path, args.width, args.quality, args.crop, args.suffix) for path in args.results)
    print(f"{total} images -> {TARGET}")


if __name__ == "__main__":
    main()
