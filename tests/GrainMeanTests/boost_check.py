"""Does Highlight boost change the image for a Capture One source? Renders the captured copy-only fixture
with halation on and several boost/protection settings; reports mean change versus boost 0."""
import json, sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
from compare_renderers import Worker

exe, fixture_json = sys.argv[1], Path(sys.argv[2])
extra_cases = sys.argv[3:]  # optional name=value overrides, applied to every case
fx = json.loads(fixture_json.read_text(encoding="utf-8-sig"))["result"]
w, h = fx["width"], fx["height"]
img = np.frombuffer(Path(fx["path"]).read_bytes(), "<f4").reshape(h, w, 4)
base = dict(zip([k[2:] for k in fx["options"][::2]], fx["options"][1::2]))
base.update({"output-color": "linear_rec709", "halation-amount": "2", "grain-model": "off"})
for item in extra_cases:
    k, v = item.split("=", 1); base[k] = v
cases = [
    ("boost 0", {"halation-boost": "0"}),
    ("boost 4, protect 4 (default)", {"halation-boost": "4", "halation-protect": "4"}),
    ("boost 4, protect 2", {"halation-boost": "4", "halation-protect": "2"}),
    ("boost 4, protect 1", {"halation-boost": "4", "halation-protect": "1"}),
]
if "--relative" in fx.get("flags", []) or any(k == "halation-protect-relative" for k in base):
    pass
wk = Worker(exe)
try:
    ref = None
    for name, opts in cases:
        out = wk.render(img, {**base, **opts})[..., :3].astype(np.float64)
        if ref is None:
            ref = out
        d = np.abs(out - ref)
        top = out.max()
        print(f"{name:32s} meanAbsChange={d.mean():.6f} maxAbsChange={d.max():.4f} pixelsChanged={(d > 1e-6).mean() * 100:.2f}%", flush=True)
finally:
    wk.close()
