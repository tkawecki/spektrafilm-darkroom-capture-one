"""Measure how Spektrafilm grain shifts the mean print colour at different output resolutions.

Drives a copy of SpektraStillRenderer over its --export-stream FRAME protocol with synthetic flat
patches (no photos, no C1). For each resolution renders grain Off and grain On and reports the
per-channel mean ratio grain/clean inside each patch, in linear Rec.709 output.
"""
import argparse, json, os, struct, subprocess, sys
from pathlib import Path
import numpy as np

# Linear-light source patches (encoded later as sRGB): greys and saturated colours.
PATCHES = [
    ("shadow", (0.03, 0.03, 0.03)), ("mid grey", (0.18, 0.18, 0.18)), ("light", (0.6, 0.6, 0.6)),
    ("sky", (0.10, 0.22, 0.55)), ("foliage", (0.08, 0.20, 0.05)), ("skin", (0.45, 0.28, 0.20)),
    ("red", (0.50, 0.05, 0.04)), ("deep blue", (0.02, 0.03, 0.20)),
]
COLS, ROWS = 4, 2


def srgb_encode(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def make_image(w, h):
    img = np.ones((h, w, 4), np.float32)
    for i, (_, rgb) in enumerate(PATCHES):
        c, r = i % COLS, i // COLS
        img[r * h // ROWS:(r + 1) * h // ROWS, c * w // COLS:(c + 1) * w // COLS, :3] = srgb_encode(np.array(rgb))
    return img


def patch_means(out, w, h):
    res = []
    for i in range(len(PATCHES)):
        c, r = i % COLS, i // COLS
        x0, x1 = c * w // COLS, (c + 1) * w // COLS
        y0, y1 = r * h // ROWS, (r + 1) * h // ROWS
        mx, my = (x1 - x0) // 5, (y1 - y0) // 5
        res.append(out[y0 + my:y1 - my, x0 + mx:x1 - mx, :3].astype(np.float64).mean(axis=(0, 1)))
    return np.array(res)


def read_exact(stream, n):
    buf = bytearray(n); view = memoryview(buf); off = 0
    while off < n:
        k = stream.readinto(view[off:])
        if not k:
            raise RuntimeError("renderer closed")
        off += k
    return bytes(buf)


def render(worker, img, opts):
    h, w = img.shape[:2]
    header = f"FRAME {w} {h} " + " ".join(f"--{k} {v}" for k, v in opts.items()) + "\n"
    worker.stdin.write(header.encode("ascii"))
    worker.stdin.write(img.astype("<f4").tobytes())
    worker.stdin.flush()
    code, length = struct.unpack("<ii", read_exact(worker.stdout, 8))
    data = read_exact(worker.stdout, length)
    if code:
        raise RuntimeError(data.decode("utf-8", "replace"))
    return np.frombuffer(data, "<f4").reshape(h, w, 4)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--exe", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--sizes", default="6000x4000,3000x2000,1500x1000,750x500")
    p.add_argument("--grain", default="production")
    p.add_argument("--set", action="append", default=[])
    a = p.parse_args()
    base = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
            "output-color": "linear_rec709", "auto-exposure": "false", "film-format": "35", "grain-seed": "1",
            "halation-amount": "0", "scatter-amount": "0"}
    for item in a.set:
        k, v = item.split("=", 1); base[k] = v
    env = {k: v for k, v in os.environ.items() if not k.startswith("SPEKTRAFILM_")}
    exe = Path(a.exe).resolve()
    worker = subprocess.Popen([str(exe), "--export-stream"], cwd=exe.parent, env=env, stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
    report = {"exe": str(exe), "options": base, "grain": a.grain, "sizes": []}
    try:
        for size in a.sizes.split(","):
            w, h = map(int, size.split("x"))
            img = make_image(w, h)
            clean = patch_means(render(worker, img, {**base, "grain-model": "off"}), w, h)
            grainy = patch_means(render(worker, img, {**base, "grain-model": a.grain}), w, h)
            ratio = grainy / np.maximum(clean, 1e-9)
            entry = {"size": size, "patches": {name: {"clean": clean[i].round(5).tolist(), "ratio": ratio[i].round(4).tolist()}
                                                for i, (name, _) in enumerate(PATCHES)}}
            entry["maxAbsShiftPct"] = float(np.abs(ratio - 1).max() * 100)
            report["sizes"].append(entry)
            print(size, "max |shift| %.2f%%" % entry["maxAbsShiftPct"],
                  " ".join(f"{n}:{'/'.join(f'{v:.3f}' for v in ratio[i])}" for i, (n, _) in enumerate(PATCHES)), flush=True)
        worker.stdin.write(b"QUIT\n"); worker.stdin.flush()
    finally:
        worker.stdin.close(); worker.wait(timeout=60)
    Path(a.out).write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
