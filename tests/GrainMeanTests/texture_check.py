"""Check that the mean restore keeps grain texture: per-patch noise (std) and its spatial spectrum."""
import argparse, json, sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
from measure_grain_mean import PATCHES, COLS, ROWS, make_image
from compare_renderers import Worker


def patch_stats(out, w, h):
    res = []
    for i in range(len(PATCHES)):
        c, r = i % COLS, i // COLS
        x0, x1 = c * w // COLS, (c + 1) * w // COLS
        y0, y1 = r * h // ROWS, (r + 1) * h // ROWS
        mx, my = (x1 - x0) // 5, (y1 - y0) // 5
        p = out[y0 + my:y1 - my, x0 + mx:x1 - mx, 1].astype(np.float64)  # green channel
        # band energies: fine (<4 px) and coarse (block means over 16 px)
        k = 16
        hh, ww = (p.shape[0] // k) * k, (p.shape[1] // k) * k
        blocks = p[:hh, :ww].reshape(hh // k, k, ww // k, k).mean(axis=(1, 3))
        res.append({"std": p.std(), "coarseStd": blocks.std(), "mean": p.mean()})
    return res


def main():
    a = argparse.ArgumentParser(); a.add_argument("--exe"); a.add_argument("--size", default="3000x2000"); a.add_argument("--grain", default="production")
    a = a.parse_args()
    w, h = map(int, a.size.split("x")); img = make_image(w, h)
    base = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
            "output-color": "linear_rec709", "auto-exposure": "false", "film-format": "35", "grain-seed": "1",
            "halation-amount": "0", "scatter-amount": "0"}
    wk = Worker(a.exe)
    try:
        off = patch_stats(wk.render(img, {**base, "grain-model": "off"}), w, h)
        on = patch_stats(wk.render(img, {**base, "grain-model": a.grain}), w, h)
        fix = patch_stats(wk.render(img, {**base, "grain-model": a.grain, "grain-mean-restore": "true"}), w, h)
    finally:
        wk.close()
    for i, (name, _) in enumerate(PATCHES):
        rel = lambda s: s["std"] / max(s["mean"], 1e-9)
        print(f"{name:10s} relStd off={rel(off[i]):.4f} grain={rel(on[i]):.4f} restored={rel(fix[i]):.4f} "
              f"coarse16 grain={on[i]['coarseStd']/max(on[i]['mean'],1e-9):.4f} restored={fix[i]['coarseStd']/max(fix[i]['mean'],1e-9):.4f}")


if __name__ == "__main__":
    main()
