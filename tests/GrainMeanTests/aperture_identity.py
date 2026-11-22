"""Aperture on/off: bit-identical at and below the 6 um reference pixel, different above it."""
import sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_renderers import Worker, scene  # noqa: E402

BASE = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
        "output-color": "srgb", "auto-exposure": "true", "film-format": "35", "grain-seed": "7",
        "grain-model": "synthesis", "grain-synthesis-samples": "32", "grain-mean-restore": "true"}
wk = Worker(sys.argv[1])
ok = True
try:
    for (w, h), expect_same in (((6000, 4000), True), ((6720, 4480), True), ((3000, 2000), False), ((1200, 800), False)):
        img = scene(w, h)
        off = wk.render(img, {**BASE, "grain-synthesis-aperture": "false"})
        on = wk.render(img, {**BASE, "grain-synthesis-aperture": "true"})
        same = np.array_equal(off, on)
        ok &= same == expect_same and np.isfinite(on).all()
        print(f"{w}x{h} identical={same} expected={expect_same} meanAbsDiff={np.abs(off - on).mean():.5f}")
finally:
    wk.close()
print("PASS" if ok else "FAIL")
sys.exit(0 if ok else 1)
