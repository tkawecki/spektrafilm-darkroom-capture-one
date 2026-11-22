"""Tiled vs full-frame equality for the grain mean restore, and the same check without it (control)."""
import sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
from compare_renderers import Worker, scene, BASE

exe = sys.argv[1]
w, h = 4000, 2667
img = scene(w, h)
full = Worker(exe)
tiled = Worker(exe, env_extra={"SPEKTRAFILM_VULKAN_TILE_WIDTH": "512", "SPEKTRAFILM_VULKAN_TILE_HEIGHT": "256"})
try:
    for restore in ("false", "true"):
        for extra in ({"grain-model": "production"}, {"grain-model": "production", "print-diffusion-family": "black_pro_mist", "print-diffusion-strength": "1", "scanner-enabled": "true", "scanner-mtf": "60"}):
            opts = {**BASE, **extra, "grain-mean-restore": restore}
            a = full.render(img, opts); b = tiled.render(img, opts)
            d = np.abs(a[..., :3] - b[..., :3])
            print(f"restore={restore} {list(extra)[-1]:24s} maxAbs={d.max():.3e} meanAbs={d.mean():.3e}", flush=True)
finally:
    full.close(); tiled.close()
