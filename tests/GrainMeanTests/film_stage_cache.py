"""Developed-negative cache of the still renderer's Viewer stream: renders that reuse the cached negative must be
bit-identical to renders made from scratch.

Usage: python film_stage_cache.py <renderer.exe> <args.json> [source.f32 width height]

For several looks (grain models, grain mean restore, print diffusion, scanner, film models), the stream gets a
FRAME, then REUSE requests that change one print, scan or output option at a time (the renderer may reuse the
negative) and, between them, film options (it must develop again). Each result is compared with a fresh render of
the same arguments in a second renderer process. Also reports the speed of a print-side change with and without
the cache.
"""
import json
import struct
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_renderers import Worker, read_exact  # noqa: E402

exe, args_path = sys.argv[1], sys.argv[2]
flat = json.loads(open(args_path).read())
base = {flat[i][2:]: flat[i + 1] for i in range(0, len(flat), 2)}
base.update({"input-color": "prophoto", "output-color": "srgb", "auto-exposure": "true"})
if len(sys.argv) > 5:
    w, h = int(sys.argv[4]), int(sys.argv[5])
    image = np.fromfile(sys.argv[3], "<f4").reshape(h, w, 4)[:1067, :1600].copy()
else:
    rng = np.random.default_rng(3)
    image = np.ones((1067, 1600, 4), np.float32)
    image[..., :3] = np.clip(rng.lognormal(-1.8, 1.0, (1067, 1600, 3)), 0, 1)
    image[400:460, 700:760, :3] = 1.0

LOOKS = {
    "production grain, mean restore, diffusion, halation, DIR": {
        "grain-model": "production", "grain-mean-restore": "true", "camera-diffusion-family": "black_pro_mist",
        "camera-diffusion-strength": "0.5", "halation-amount": "4", "scatter-amount": "1", "dir-diffusion": "1"},
    "print diffusion and scanner": {
        "grain-model": "production", "print-diffusion-family": "glimmerglass", "print-diffusion-strength": "0.5",
        "scanner-enabled": "true", "scanner-glare": "0.03", "scanner-unsharp-amount": "0.7"},
    "preview grain, film model 2": {"grain-model": "preview", "film-model": "2"},
    "grain synthesis": {"grain-model": "synthesis", "grain-synthesis-samples": "32"},
    "no grain": {"grain-model": "off"},
    "DIR without grain (final = grain-free density)": {"grain-model": "off", "dir-diffusion": "1", "dir-amount": "1"},
    "scan mode, slide film": {"mode": "scan", "stock": "fujifilm_velvia_100", "grain-model": "production"},
    "mean restore switched by output colour": {"grain-model": "production", "grain-mean-restore": "true"},
}
PRINT_CHANGES = [("print-exposure", "0.3"), ("paper", "kodak_supra_endura"), ("cyan-filter", "0.05"),
                 ("magenta-filter", "-0.1"), ("yellow-filter", "0.1"), ("print-gamma", "1.2"), ("preflash", "0.02"),
                 ("print-exhaustion", "0.3"), ("scanner-enabled", "true"), ("scanner-white-level", "0.9"),
                 ("print-diffusion-family", "pro_mist"), ("print-diffusion-strength", "0.3"),
                 ("color-output-chroma-compression", "false"), ("output-color", "linear_rec709"),
                 ("output-color", "srgb")]
FILM_CHANGES = [("film-exposure", "0.5"), ("grain-seed", "7"), ("halation-amount", "2")]


def request(worker: Worker, verb: str, opts: dict, frame: "np.ndarray | None" = None) -> np.ndarray:
    header = verb + (f" {frame.shape[1]} {frame.shape[0]}" if frame is not None else "")
    worker.p.stdin.write((header + " " + " ".join(f"--{k} {v}" for k, v in opts.items()) + "\n").encode("ascii"))
    if frame is not None:
        worker.p.stdin.write(frame.astype("<f4").tobytes())
    worker.p.stdin.flush()
    code, length = struct.unpack("<ii", read_exact(worker.p.stdout, 8))
    data = read_exact(worker.p.stdout, length)
    if code:
        raise RuntimeError(data.decode("utf-8", "replace"))
    return np.frombuffer(data, "<f4").reshape(image.shape[0], image.shape[1], 4)


warm = Worker(exe, "--native-preview-stream")
cold = Worker(exe, "--native-preview-stream")
checks = failures = 0
try:
    for name, look in LOOKS.items():
        opts = dict(base, **look)
        request(warm, "FRAME", opts, image)
        sequence = []
        for i, change in enumerate(PRINT_CHANGES):
            sequence.append(("print", change))
            if i % 4 == 3:
                sequence.append(("film", FILM_CHANGES[(i // 4) % len(FILM_CHANGES)]))
        current = dict(opts)
        for kind, (key, value) in sequence:
            current = dict(current, **{key: value})
            got = request(warm, "REUSE", current)
            want = request(cold, "FRAME", current, image)
            diff = float(np.abs(got - want).max())
            checks += 1
            if diff != 0.0:
                failures += 1
                print(f"FAIL {name}: {kind} change {key}={value}: max diff {diff:.3g}")
        print(f"{name}: {len(sequence)} changes compared")
    # Speed of a print-side change: cache on (REUSE after a FRAME) versus a film-side change (develops again).
    opts = dict(base, **LOOKS["production grain, mean restore, diffusion, halation, DIR"])
    request(warm, "FRAME", opts, image)
    timings = {}
    for kind, key in (("print-side change (cache)", "print-exposure"), ("film-side change (no cache)", "film-exposure")):
        ts = []
        for i in range(8):
            t = time.perf_counter(); request(warm, "REUSE", dict(opts, **{key: f"{0.01 * (i + 1):.2f}"})); ts.append(time.perf_counter() - t)
        timings[kind] = round(float(np.median(ts[2:])) * 1000, 1)
    print("1600x1067 request time incl. float readback:", timings)
    # Source A, then B, then A again: the second A is a new frame and must not reuse B's (or A's old) negative.
    other = np.ascontiguousarray(image[::-1])
    for frame in (image, other, image):
        request(warm, "FRAME", opts, frame)
        got = request(warm, "REUSE", dict(opts, **{"print-exposure": "0.2"}))
        want = request(cold, "FRAME", dict(opts, **{"print-exposure": "0.2"}), frame)
        checks += 1
        if float(np.abs(got - want).max()) != 0.0:
            failures += 1
            print("FAIL source switch: cached render differs")
finally:
    warm.close()
    cold.close()
print(("PASS" if failures == 0 else "FAIL") + f" {checks - failures}/{checks} cached renders bit-identical to fresh renders")
sys.exit(1 if failures else 0)
