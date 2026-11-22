"""Verify added colour profiles and legacy renderer compatibility, without C1."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "ofx-still-renderer"))
from smoke import frame_request, source_frame

NEW_FILMS = (
    "fujifilm_eterna_250d", "fujifilm_velvia_100f", "kodak_ultramax_800",
    "kodak_vision2_100t", "kodak_vision2_500t", "kodak_vision2_50d",
)


def render_set(exe: Path, frame: np.ndarray, options: list[str]) -> list[np.ndarray]:
    request = frame_request(frame, options[0])
    request += b"".join(f"REUSE {option}\n".encode("ascii") for option in options[1:])
    result = subprocess.run([str(exe), "--stream"], input=request, capture_output=True, timeout=300)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    offset, frames = 0, []
    for option in options:
        status, length = struct.unpack_from("<II", result.stdout, offset)
        offset += 8
        payload = result.stdout[offset:offset + length]
        offset += length
        if status:
            raise RuntimeError(f"{option}: {payload.decode('utf-8', errors='replace')}")
        assert length == frame.nbytes
        pixels = np.frombuffer(payload, dtype="<f4").reshape(frame.shape)
        assert np.isfinite(pixels).all(), option
        np.testing.assert_array_equal(pixels[..., 3], frame[..., 3])
        frames.append(pixels)
    assert offset == len(result.stdout)
    return frames


def run(baseline: Path, candidate: Path, output: Path) -> None:
    spec = importlib.util.spec_from_file_location("ofx_stock_lists", ROOT / "upstream-ofx/tools/ofx_stock_lists.py")
    stocks = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(stocks)
    legacy = stocks._LEGACY_FILM_ORDER
    frame = source_frame(None)[::4, ::4].copy()
    base = "--paper kodak_portra_endura --input-color srgb --output-color srgb --grain-model off --halation-amount 0 --scatter-amount 0 --auto-exposure 0 --color-adaptation 0"
    evidence = {"baseline": str(baseline), "candidate": str(candidate), "legacy": [], "added": []}
    methods = ("hanatos2025", "hanatos2026", "mallett2019")
    old_cases = [(stock, mode, method) for method in methods for stock in legacy for mode in ("print", "scan")]
    new_cases = [(stock, mode, method) for method in methods for mode in ("print", "scan") for stock in NEW_FILMS]
    options = lambda cases: [f"{base} --stock {stock} --mode {mode} --spectral-method {method}" for stock, mode, method in cases]
    before_frames = render_set(baseline, frame, options(old_cases))
    print("Legacy baseline rendered", flush=True)
    after_frames = render_set(candidate, frame, options(old_cases + new_cases))
    for (stock, mode, method), before, after in zip(old_cases, before_frames, after_frames):
        np.testing.assert_array_equal(after, before, err_msg=f"Legacy profile changed: {stock}/{mode}/{method}")
        evidence["legacy"].append({"stock": stock, "mode": mode, "method": method, "sha256": hashlib.sha256(after.tobytes()).hexdigest()})
    seen = {}
    for (stock, mode, method), after in zip(new_cases, after_frames[len(old_cases):]):
        digest = hashlib.sha256(after.tobytes()).hexdigest()
        group = seen.setdefault((mode, method), set())
        assert digest not in group, ("Duplicate added profile output", stock, mode, method)
        group.add(digest)
        evidence["added"].append({"stock": stock, "mode": mode, "method": method, "sha256": digest, "min": float(after[..., :3].min()), "max": float(after[..., :3].max())})
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(f"PASS: {len(evidence['legacy'])} legacy renders bit-exact; {len(evidence['added'])} added renders finite and unique within each mode")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    run(args.baseline.resolve(), args.candidate.resolve(), args.output)
