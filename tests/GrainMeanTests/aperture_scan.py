"""Grain Synthesis: mean shift and grain texture versus observation window and output resolution.

For each window rule (fixed um, or proportional to the film pixel) and size renders grain off, grain on and
grain on with the mean restore. Reports the per-patch mean ratio and green grain std. The 6000 px render is
also box-downscaled to every smaller size, so native low-resolution grain can be compared with a downscaled
full-resolution export (what a preview should look like).
"""
import argparse, json, sys, time
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
from measure_grain_mean import PATCHES, COLS, ROWS, make_image, render  # noqa: E402
import subprocess, os  # noqa: E402

FILM_MM = 36.0
NAMES = ["mid grey", "light", "skin", "sky"]


def boxes(w, h):
    for i in range(len(PATCHES)):
        c, r = i % COLS, i // COLS
        x0, x1 = c * w // COLS, (c + 1) * w // COLS
        y0, y1 = r * h // ROWS, (r + 1) * h // ROWS
        mx, my = (x1 - x0) // 5, (y1 - y0) // 5
        yield PATCHES[i][0], (slice(y0 + my, y1 - my), slice(x0 + mx, x1 - mx))


def stats(out, w, h):
    res = {}
    for name, (sy, sx) in boxes(w, h):
        p = out[sy, sx, :3].astype(np.float64)
        res[name] = {"mean": p.mean(axis=(0, 1)), "relStdG": p[..., 1].std() / max(p[..., 1].mean(), 1e-9)}
    return res


def downscale(img, k):
    h, w = img.shape[0] // k * k, img.shape[1] // k * k
    return img[:h, :w].reshape(h // k, k, w // k, k, img.shape[2]).mean(axis=(1, 3))


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--exe", required=True)
    a.add_argument("--out", required=True)
    a.add_argument("--rule", required=True, help="fixed:<um> or pixel:<k>")
    a.add_argument("--sizes", default="6000x4000,3000x2000,1500x1000,750x500")
    a.add_argument("--set", action="append", default=[])
    a = a.parse_args()
    kind, val = a.rule.split(":"); val = float(val)
    base = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
            "output-color": "linear_rec709", "auto-exposure": "false", "film-format": "35", "grain-seed": "1",
            "halation-amount": "0", "scatter-amount": "0"}
    for item in a.set:
        k, v = item.split("=", 1); base[k] = v
    env = {k: v for k, v in os.environ.items() if not k.startswith("SPEKTRAFILM_")}
    exe = Path(a.exe).resolve()
    wk = subprocess.Popen([str(exe), "--export-stream"], cwd=exe.parent, env=env, stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
    report = {"rule": a.rule, "sizes": {}}
    full = None
    try:
        for size in a.sizes.split(","):
            w, h = map(int, size.split("x"))
            pixel_um = FILM_MM * 1000 / w
            obs = val if kind == "fixed" else (max(1.0, val * pixel_um) if kind == "pixelmin" else val * pixel_um)
            img = make_image(w, h)
            opts = {**base, "grain-synthesis-observation": f"{min(obs, 20.0):.4f}"}
            clean = render(wk, img, {**opts, "grain-model": "off"})
            t0 = time.perf_counter(); grainy = render(wk, img, {**opts, "grain-model": "synthesis"}); t = time.perf_counter() - t0
            fixed = render(wk, img, {**opts, "grain-model": "synthesis", "grain-mean-restore": "true"})
            sc, sg, sf = stats(clean, w, h), stats(grainy, w, h), stats(fixed, w, h)
            entry = {"observationUm": min(obs, 20.0), "grainSeconds": round(t, 3), "patches": {}}
            for n in NAMES:
                entry["patches"][n] = {"ratio": (sg[n]["mean"] / sc[n]["mean"]).round(3).tolist(),
                                       "ratioRestore": (sf[n]["mean"] / sc[n]["mean"]).round(3).tolist(),
                                       "relStdG": round(sg[n]["relStdG"], 4), "relStdGRestore": round(sf[n]["relStdG"], 4)}
            if full is None:
                full = (w, fixed)
            else:
                k = full[0] // w
                ds = stats(downscale(full[1], k), w, h)
                for n in NAMES:
                    entry["patches"][n]["relStdGDownscaledFull"] = round(ds[n]["relStdG"], 4)
            report["sizes"][size] = entry
            print(size, f"obs {entry['observationUm']:.2f}um t {t:.2f}s", " | ".join(
                f"{n}: {'/'.join(f'{v:.3f}' for v in e['ratio'])} -> {'/'.join(f'{v:.3f}' for v in e['ratioRestore'])}"
                f" std {e['relStdGRestore']:.3f}" + (f" (ds {e['relStdGDownscaledFull']:.3f})" if 'relStdGDownscaledFull' in e else "")
                for n, e in entry["patches"].items()), flush=True)
        wk.stdin.write(b"QUIT\n"); wk.stdin.flush()
    finally:
        wk.stdin.close(); wk.wait(timeout=60)
    Path(a.out).write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
