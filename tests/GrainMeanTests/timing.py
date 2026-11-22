"""Warm render time with and without the grain mean restore (export stream, float output)."""
import statistics, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
from compare_renderers import Worker, scene, BASE

exe, size = sys.argv[1], sys.argv[2]
w, h = map(int, size.split("x"))
img = scene(w, h)
wk = Worker(exe)
try:
    for label, extra in [("grain off", {"grain-model": "off"}), ("production", {"grain-model": "production"}),
                         ("production+restore", {"grain-model": "production", "grain-mean-restore": "true"})]:
        times = []
        for i in range(4):
            opts = {**BASE, **extra, "film-exposure": str(0.01 * i)}
            t = time.perf_counter(); wk.render(img, opts); times.append(time.perf_counter() - t)
        print(f"{size} {label:20s} median warm {statistics.median(times[1:]) * 1000:.0f} ms", flush=True)
finally:
    wk.close()
