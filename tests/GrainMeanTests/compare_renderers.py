"""Compare two SpektraStillRenderer builds bit-for-bit on synthetic images across configurations.

Also reports, for the candidate with --grain-mean-restore true, the mean shift versus grain off.
"""
import argparse, hashlib, json, os, struct, subprocess
from pathlib import Path
import numpy as np


def read_exact(stream, n):
    buf = bytearray(n); view = memoryview(buf); off = 0
    while off < n:
        k = stream.readinto(view[off:])
        if not k:
            raise RuntimeError("renderer closed")
        off += k
    return bytes(buf)


class Worker:
    def __init__(self, exe, mode="--export-stream", env_extra=None):
        exe = Path(exe).resolve()
        env = {k: v for k, v in os.environ.items() if not k.startswith("SPEKTRAFILM_")}
        env.update(env_extra or {})
        self.p = subprocess.Popen([str(exe), mode], cwd=exe.parent, env=env, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)

    def render(self, img, opts):
        h, w = img.shape[:2]
        header = f"FRAME {w} {h} " + " ".join(f"--{k} {v}" for k, v in opts.items()) + "\n"
        self.p.stdin.write(header.encode("ascii")); self.p.stdin.write(img.astype("<f4").tobytes()); self.p.stdin.flush()
        code, length = struct.unpack("<ii", read_exact(self.p.stdout, 8))
        data = read_exact(self.p.stdout, length)
        if code:
            raise RuntimeError(data.decode("utf-8", "replace"))
        return np.frombuffer(data, "<f4").reshape(h, w, 4)

    def close(self):
        try:
            self.p.stdin.write(b"QUIT\n"); self.p.stdin.flush(); self.p.stdin.close(); self.p.wait(timeout=60)
        except Exception:
            self.p.kill()


def scene(w, h, seed=3):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    img = np.ones((h, w, 4), np.float32)
    img[..., 0] = 0.5 + 0.45 * np.sin(x / 37.0) * np.cos(y / 53.0)
    img[..., 1] = (x / w) ** 1.5
    img[..., 2] = (y / h)
    img[h // 3:h // 2, w // 4:3 * w // 4, :3] = [0.98, 0.95, 0.9]  # bright bar for halation/diffusion
    img[..., :3] = np.clip(img[..., :3] + rng.normal(0, 0.01, (h, w, 3)), 0, 1)
    return img


BASE = {"stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "srgb",
        "output-color": "srgb", "auto-exposure": "true", "film-format": "35", "grain-seed": "7"}
CONFIGS = {
    "grain off": {"grain-model": "off"},
    "production": {"grain-model": "production"},
    "preview grain": {"grain-model": "preview"},
    "rms": {"grain-model": "rms"},
    "synthesis": {"grain-model": "synthesis", "grain-synthesis-samples": "8"},
    "production+scanner": {"grain-model": "production", "scanner-enabled": "true", "scanner-mtf": "60", "scanner-unsharp-radius": "5", "scanner-unsharp-amount": "0.7", "scanner-glare": "0.03"},
    "production+print diffusion": {"grain-model": "production", "print-diffusion-family": "black_pro_mist", "print-diffusion-strength": "1"},
    "production+halation+dir": {"grain-model": "production", "halation-amount": "2", "scatter-amount": "1", "dir-diffusion": "20"},
    "scan negative": {"grain-model": "production", "mode": "scan"},
    "scan slide": {"grain-model": "production", "mode": "scan", "stock": "fujifilm_provia_100f"},
    "linear output": {"grain-model": "production", "output-color": "linear_rec709"},
}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--baseline", required=True); p.add_argument("--candidate", required=True)
    p.add_argument("--size", default="1500x1000"); p.add_argument("--out", required=True)
    p.add_argument("--tile", type=int, default=0)
    a = p.parse_args()
    w, h = map(int, a.size.split("x")); img = scene(w, h)
    env = {"SPEKTRAFILM_VULKAN_TILE_WIDTH": str(a.tile), "SPEKTRAFILM_VULKAN_TILE_HEIGHT": str(a.tile)} if a.tile else {}
    base, cand = Worker(a.baseline, env_extra=env), Worker(a.candidate, env_extra=env)
    report = {"size": a.size, "tile": a.tile, "identicalOff": {}, "restore": {}}
    try:
        for name, extra in CONFIGS.items():
            opts = {**BASE, **extra}
            b = base.render(img, opts); c = cand.render(img, opts)
            same = hashlib.sha256(b.tobytes()).hexdigest() == hashlib.sha256(c.tobytes()).hexdigest()
            report["identicalOff"][name] = same
            r = cand.render(img, {**opts, "grain-mean-restore": "true"})
            finite = bool(np.isfinite(r).all())
            diff = float(np.abs(r[..., :3] - c[..., :3]).mean())
            report["restore"][name] = {"finite": finite, "meanAbsChange": round(diff, 6)}
            print(f"{name:28s} off-identical={same} restore finite={finite} meanAbsChange={diff:.5f}", flush=True)
    finally:
        base.close(); cand.close()
    Path(a.out).write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
