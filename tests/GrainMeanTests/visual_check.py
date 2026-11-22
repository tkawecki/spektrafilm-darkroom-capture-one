"""Render the captured C1 test fixture with Production grain, restore off/on, and save 1:1 crops."""
import json, sys, zlib, struct
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
from compare_renderers import Worker

exe, fixture_json, out_dir = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
fx = json.loads(fixture_json.read_text(encoding="utf-8-sig"))["result"]
w, h = fx["width"], fx["height"]
img = np.frombuffer(Path(fx["path"]).read_bytes(), "<f4").reshape(h, w, 4)
opts = dict(zip([k[2:] for k in fx["options"][::2]], fx["options"][1::2]))
opts.update({"grain-model": "production", "output-color": "srgb", "grain-seed": "1"})


def png(path, rgb):
    a = (np.clip(rgb, 0, 1) * 255 + 0.5).astype(np.uint8)
    raw = b"".join(b"\x00" + a[y].tobytes() for y in range(a.shape[0]))
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", a.shape[1], a.shape[0], 8, 2, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


wk = Worker(exe)
try:
    off = wk.render(img, {**opts, "grain-mean-restore": "false"})[..., :3]
    on = wk.render(img, {**opts, "grain-mean-restore": "true"})[..., :3]
    clean = wk.render(img, {**opts, "grain-model": "off"})[..., :3]
finally:
    wk.close()
out_dir.mkdir(parents=True, exist_ok=True)
# Pick the 512x512 window with the strongest local contrast for an edge check.
lum = clean.mean(axis=2)
best, pos = -1, (0, 0)
for y in range(0, h - 512, 256):
    for x in range(0, w - 512, 256):
        s = lum[y:y + 512, x:x + 512].std()
        if s > best: best, pos = s, (y, x)
y, x = pos
crop = lambda a: a[y:y + 512, x:x + 512]
png(out_dir / "crop.png", np.concatenate([crop(off), crop(on), crop(clean)], axis=1))
diff = crop(on) - crop(off)
png(out_dir / "crop-diff-x8.png", 0.5 + 8 * diff)
blur = lambda a: a.reshape(h // 16, 16, w // 16, 16, 3).mean(axis=(1, 3)) if h % 16 == 0 and w % 16 == 0 else None
print(json.dumps({"size": [w, h], "crop": [x, y],
                  "meanOff": off.mean(axis=(0, 1)).round(4).tolist(), "meanOn": on.mean(axis=(0, 1)).round(4).tolist(),
                  "meanClean": clean.mean(axis=(0, 1)).round(4).tolist(),
                  "maxAbsDiffOnOff": float(np.abs(on - off).max())}))
