"""Grain synthesis noise matching: bit-identical wherever it must not act.

Same pixels with and without --grain-synthesis-noise-match at the 6 um reference pixel and above (6000x4000,
6720x4480) with aperture integration, and at any size without aperture integration; different at 3000 and
1200 px with aperture integration.
"""
import sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_renderers import Worker, scene  # noqa: E402

BASE = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
        "output-color": "srgb", "auto-exposure": "true", "film-format": "35", "grain-seed": "7",
        "grain-model": "synthesis", "grain-synthesis-samples": "32", "grain-mean-restore": "true"}
CASES = (((6000, 4000), "true", True), ((6720, 4480), "true", True), ((3000, 2000), "true", False),
         ((1200, 800), "true", False), ((1200, 800), "false", True))
wk = Worker(sys.argv[1])
ok = True
try:
    for (w, h), aperture, expect_same in CASES:
        img = scene(w, h)
        opts = {**BASE, "grain-synthesis-aperture": aperture}
        off = wk.render(img, {**opts, "grain-synthesis-noise-match": "false"})
        on = wk.render(img, {**opts, "grain-synthesis-noise-match": "true"})
        same = np.array_equal(off, on)
        ok &= same == expect_same and np.isfinite(on).all()
        print(f"{w}x{h} aperture={aperture} identical={same} expected={expect_same} meanAbsDiff={np.abs(off - on).mean():.5f}")
finally:
    wk.close()
print("PASS" if ok else "FAIL")
sys.exit(0 if ok else 1)
