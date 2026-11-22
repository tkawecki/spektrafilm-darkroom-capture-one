"""Crops of the brightest region of the captured fixture: halation without boost, with boost and the
original 4 EV mid-grey protection, and with boost measured from the brightest highlight."""
import json
import struct
import sys
import zlib
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from compare_renderers import Worker  # noqa: E402


def png(path, rgb):
    a = (np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8)
    raw = b"".join(b"\x00" + a[y].tobytes() for y in range(a.shape[0]))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)

    header = struct.pack(">IIBBBBB", a.shape[1], a.shape[0], 8, 2, 0, 0, 0)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


def main():
    exe, fixture_json, out_dir = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
    fx = json.loads(fixture_json.read_text(encoding="utf-8-sig"))["result"]
    w, h = fx["width"], fx["height"]
    img = np.frombuffer(Path(fx["path"]).read_bytes(), "<f4").reshape(h, w, 4)
    base = dict(zip([k[2:] for k in fx["options"][::2]], fx["options"][1::2]))
    base.update({"output-color": "srgb", "halation-amount": "2", "grain-model": "off"})
    wk = Worker(exe)
    try:
        a = wk.render(img, {**base, "halation-boost": "0"})[..., :3]
        b = wk.render(img, {**base, "halation-boost": "4", "halation-protect": "4"})[..., :3]
        c = wk.render(img, {**base, "halation-boost": "4", "halation-protect": "2", "halation-protect-relative": "true"})[..., :3]
    finally:
        wk.close()
    lum = img[..., :3].mean(axis=2)
    best, pos = -1.0, (0, 0)
    for y in range(0, h - 512, 128):
        for x in range(0, w - 512, 128):
            score = float((lum[y:y + 512, x:x + 512] > 0.75).mean())
            if 0.05 < score < 0.5 and score > best:
                best, pos = score, (y, x)
    y, x = pos
    crop = lambda arr: arr[y:y + 512, x:x + 512]  # noqa: E731
    out_dir.mkdir(parents=True, exist_ok=True)
    png(out_dir / "boost-crop.png", np.concatenate([crop(a), crop(b), crop(c)], axis=1))
    print(json.dumps({"crop": [x, y], "brightFraction": best,
                      "meanAbs_b_vs_a": float(np.abs(b - a).mean()), "meanAbs_c_vs_a": float(np.abs(c - a).mean())}))


if __name__ == "__main__":
    main()
