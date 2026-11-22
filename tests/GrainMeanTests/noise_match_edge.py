"""Grain synthesis noise matching keeps edges: column means across a sharp vertical edge at 750 px.

With noise matching (128 samples) the averaged profile must match point sampling with 640 samples (where
sampling noise is small) as closely as point sampling with 128 samples does, i.e. matching must not pull edge
pixels back toward their centre density.
"""
import sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_renderers import Worker  # noqa: E402

W, H = 750, 500
BASE = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
        "output-color": "linear_rec709", "auto-exposure": "false", "film-format": "35", "grain-seed": "3",
        "grain-model": "synthesis", "grain-mean-restore": "false", "grain-synthesis-aperture": "true",
        "halation-amount": "0", "scatter-amount": "0"}
img = np.zeros((H, W, 4), np.float32)
img[..., 3] = 1
img[:, : W // 2, :3] = 0.04
img[:, W // 2:, :3] = 0.6
wk = Worker(sys.argv[1])
try:
    def profile(**opts):
        out = wk.render(img, {**BASE, **opts})
        return out[:, W // 2 - 6: W // 2 + 6, 1].mean(axis=0)
    ref = profile(**{"grain-synthesis-samples": "640", "grain-synthesis-noise-match": "false"})
    raw = profile(**{"grain-synthesis-samples": "128", "grain-synthesis-noise-match": "false"})
    matched = profile(**{"grain-synthesis-samples": "128", "grain-synthesis-noise-match": "true"})
finally:
    wk.close()
err_raw = np.abs(raw / ref - 1)
err_matched = np.abs(matched / ref - 1)
for name, p in (("640 samples", ref), ("128 samples", raw), ("128 + matching", matched)):
    print(f"{name:16s}", " ".join(f"{v:.4f}" for v in p))
print(f"max relative deviation from 640 samples: point sampling {err_raw.max():.4f}, matching {err_matched.max():.4f}")
ok = err_matched.max() <= max(err_raw.max(), 0.02)
print("PASS" if ok else "FAIL")
sys.exit(0 if ok else 1)
