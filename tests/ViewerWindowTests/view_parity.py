"""Viewer windows (VIEW/VIEW8): a window of the resident frame must equal the same pixels of the full render.

Usage: python view_parity.py <SpektraStillRenderer.exe> [source.f32 width height]

For many looks (grain models, mean restore, halation with highlight boost, camera and print diffusion, DIR, scanner,
film models, scan mode, enlarger scale) the native preview stream renders the full frame (FRAME) and several windows
(VIEW: interior, corners, edges, 1x1) of it, and compares them bit for bit; VIEW8 (packed Viewer pixels, also with
look strength) against the same rows of FRAME8. Also reports the time of a window against the full frame.

One known exception: with the halation highlight boost the tiled path (which windows use) measures the boost on the
whole frame in a separate pass, while a full frame does it inside its single pass; float results differ by up to
6e-5 (0.015/255), the same as tiled exports above 32 MP. Packed Viewer pixels are identical; the test allows 1e-4
in float for that look only.
"""
import struct
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import read_exact  # noqa: E402
import subprocess  # noqa: E402

exe = Path(sys.argv[1]).resolve()
if len(sys.argv) > 4:
    W, H = int(sys.argv[3]), int(sys.argv[4])
    image = np.fromfile(sys.argv[2], "<f4").reshape(H, W, 4).copy()
else:
    W, H = 3000, 2000
    rng = np.random.default_rng(5)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    image = np.ones((H, W, 4), np.float32)
    image[..., 0] = 0.4 + 0.35 * np.sin(xx / 97.0) * np.cos(yy / 61.0)
    image[..., 1] = (xx / W) ** 1.3
    image[..., 2] = yy / H
    image[700:760, 1200:1900, :3] = 1.0          # bright bar: halation, diffusion, glare
    image[..., :3] = np.clip(image[..., :3] + rng.normal(0, 0.01, (H, W, 3)), 0, 1)

BASE = {"input-color": "prophoto", "output-color": "srgb", "auto-exposure": "true", "film-model": "4",
        "spectral-method": "hanatos2026", "color-adaptation": "true"}
LOOKS = {
    "defaults, production grain": {"grain-model": "production"},
    "preview grain, model 1": {"grain-model": "preview", "film-model": "1"},
    "model 3, median metering": {"grain-model": "production", "film-model": "3", "auto-exposure-meter": "median"},
    "matrix metering, strong DIR": {"auto-exposure-meter": "matrix", "dir-amount": "2", "dir-same-layer": "2",
                                    "dir-interlayer": "2", "dir-diffusion": "1"},
    "RMS grain, mean restore": {"grain-model": "rms", "grain-mean-restore": "true"},
    "halation with boost, scatter": {"halation-amount": "4", "scatter-amount": "1", "halation-boost": "1.5"},
    "halation without boost": {"halation-amount": "4", "scatter-amount": "1"},
    "camera diffusion, DIR": {"camera-diffusion-family": "black_pro_mist", "camera-diffusion-strength": "0.6",
                              "dir-amount": "1", "dir-diffusion": "1"},
    "print diffusion, scanner": {"print-diffusion-family": "glimmerglass", "print-diffusion-strength": "0.5",
                                 "scanner-enabled": "true", "scanner-glare": "0.03", "scanner-unsharp-amount": "0.7"},
    "scan mode, slide": {"mode": "scan", "stock": "fujifilm_velvia_100", "grain-model": "production"},
    "enlarger scale": {"enlarger-scale": "1.3", "grain-model": "production"},
}
WINDOWS = [(1100, 600, 900, 500), (0, 0, 640, 400), (W - 500, H - 300, 500, 300), (0, 900, W, 200), (1500, 1000, 1, 1)]


class Stream:
    def __init__(self):
        self.p = subprocess.Popen([str(exe), "--native-preview-stream"], cwd=exe.parent, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)

    def ask(self, header: str, payload: bytes = b"") -> bytes:
        self.p.stdin.write(header.encode("ascii") + b"\n" + payload); self.p.stdin.flush()
        code, length = struct.unpack("<ii", read_exact(self.p.stdout, 8))
        data = read_exact(self.p.stdout, length)
        if code:
            raise RuntimeError(data.decode("utf-8", "replace"))
        return data

    def close(self):
        try:
            self.p.stdin.write(b"QUIT\n"); self.p.stdin.flush(); self.p.wait(timeout=60)
        except Exception:
            self.p.kill()


def options(o: dict) -> str:
    return " ".join(f"--{k} {v}" for k, v in o.items())


stream = Stream()
checks = failures = 0
try:
    stream.ask("HELLO 3")
    for name, look in LOOKS.items():
        o = options(dict(BASE, **look))
        full = np.frombuffer(stream.ask(f"FRAME {W} {H} {o}", image.astype("<f4").tobytes()), "<f4").reshape(H, W, 4)
        worst = 0.0
        for x, y, w, h in WINDOWS:
            view = np.frombuffer(stream.ask(f"VIEW {x} {y} {w} {h} {o}"), "<f4").reshape(h, w, 4)
            worst = max(worst, float(np.abs(view - full[y:y + h, x:x + w]).max()))
            checks += 1
        packed_full = np.frombuffer(stream.ask(f"REUSE8 {o} --display-strength 0.7"), np.uint8).reshape(H, W, 4)
        x, y, w, h = WINDOWS[0]
        packed_view = np.frombuffer(stream.ask(f"VIEW8 {x} {y} {w} {h} {o} --display-strength 0.7"), np.uint8).reshape(h, w, 4)
        packed_equal = np.array_equal(packed_view, packed_full[y:y + h, x:x + w])
        checks += 1
        tolerance = 1e-4 if "halation-boost" in look else 0.0
        bad = worst > tolerance or not packed_equal
        failures += bad
        print(f"{'FAIL' if bad else 'ok  '} {name:34s} float windows max diff {worst:.3g}, packed window equal {packed_equal}")
    # INPUT replaces the resident frame without rendering it; a window of it equals the full render.
    o = options(dict(BASE, **{"grain-model": "production"}))
    full = np.frombuffer(stream.ask(f"FRAME {W} {H} {o}", image.astype("<f4").tobytes()), "<f4").reshape(H, W, 4)
    other = np.ascontiguousarray(image[::-1])
    assert stream.ask(f"INPUT {W} {H}", other.astype("<f4").tobytes()) == b""
    assert stream.ask(f"INPUT {W} {H}", image.astype("<f4").tobytes()) == b""
    x, y, w, h = WINDOWS[0]
    view = np.frombuffer(stream.ask(f"VIEW {x} {y} {w} {h} {o}"), "<f4").reshape(h, w, 4)
    checks += 1
    input_ok = np.array_equal(view, full[y:y + h, x:x + w])
    failures += not input_ok
    print(f"{'ok  ' if input_ok else 'FAIL'} INPUT then VIEW equals the full render")
    # Timing: full frame against a screen-sized window, a print change each time.
    o = dict(BASE, **{"grain-model": "production"})
    stream.ask(f"FRAME {W} {H} {options(o)}", image.astype("<f4").tobytes())
    t_full, t_view = [], []
    for i in range(6):
        oi = options(dict(o, **{"print-exposure": f"{0.01 * (i + 1):.2f}"}))
        t = time.perf_counter(); stream.ask(f"REUSE8 {oi}"); t_full.append(time.perf_counter() - t)
        t = time.perf_counter(); stream.ask(f"VIEW8 {W // 4} {H // 4} {W // 2} {H // 2} {oi}"); t_view.append(time.perf_counter() - t)
    print(f"{W}x{H}: full frame {np.median(t_full) * 1000:.0f} ms, half-size window {np.median(t_view) * 1000:.0f} ms "
          "(packed, incl. transfer)")
finally:
    stream.close()
print(("PASS" if not failures else f"FAIL {failures}") + f" ({checks} comparisons)")
sys.exit(1 if failures else 0)
