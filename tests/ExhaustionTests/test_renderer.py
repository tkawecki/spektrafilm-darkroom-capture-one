"""Exercise paper developer exhaustion through the real Vulkan frame bridge."""
import argparse
import importlib.util
from pathlib import Path
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "upstream-ofx/tools"))
from ofx_stock_lists import PAPERS
spec = importlib.util.spec_from_file_location("smoke", ROOT / "ofx-still-renderer/smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def run(exe, baseline):
    frame = smoke.source_frame(None)[::4, ::4].copy()
    base = "--input-color srgb --output-color srgb --auto-exposure 0 --grain-model off"
    cases = [(paper, gamma, push, f"{base} --paper {paper} --print-gamma {gamma} --print-push-pull {push}")
             for paper in PAPERS for gamma, push in ((1, 0), (0.7, -2), (2, 2))]
    old_replies = smoke.run_stream(baseline, b''.join(smoke.frame_request(frame, options) for _, _, _, options in cases))
    reuse_cases = []
    max_near_zero_delta = 0
    for (paper, gamma, push, options), old_reply in zip(cases, old_replies):
        assert old_reply[0] == 0
        old = np.frombuffer(old_reply[1], dtype='<f4').reshape(frame.shape)
        amounts = (0.000001, 0.25, 0.5, 1, 0.25, 0)
        request = smoke.frame_request(frame, options + " --print-exhaustion 0")
        request += b''.join(f"REUSE {options} --print-exhaustion {amount}\n".encode() for amount in amounts)
        replies = smoke.run_stream(exe, request + b'QUIT\n')
        assert len(replies) == 8
        outputs = []
        for reply in replies[:-1]:
            assert reply[0] == 0, reply
            pixels = np.frombuffer(reply[1], dtype='<f4').reshape(frame.shape)
            assert np.isfinite(pixels).all()
            np.testing.assert_array_equal(pixels[..., 3], frame[..., 3])
            outputs.append(pixels)
        zero = outputs[0]
        np.testing.assert_array_equal(zero, old, err_msg=f"zero changed paper {paper}")
        for amount, changed in zip(amounts[:4], outputs[1:5]):
            delta = float(np.max(np.abs(changed - zero)))
            assert delta > 0, (paper, gamma, push, amount, "exhaustion had no effect")
            if amount == 0.000001:
                max_near_zero_delta = max(max_near_zero_delta, delta)
                # Tail ripples can move for tiny fitted CDF changes; their RGB
                # effect must stay below one 8-bit code value.
                assert delta < 0.001, (paper, delta, "discontinuity near zero")
        np.testing.assert_array_equal(outputs[5], outputs[2])
        np.testing.assert_array_equal(outputs[6], zero)
        reuse_cases.append((options + " --print-exhaustion 1", outputs[4]))
    stream = smoke.frame_request(frame, reuse_cases[0][0])
    stream += b''.join(f"REUSE {options}\n".encode() for options, _ in reuse_cases[1:])
    replies = smoke.run_stream(exe, stream + b'QUIT\n')
    for reply, (_, expected) in zip(replies[:-1], reuse_cases):
        assert reply[0] == 0, reply
        np.testing.assert_array_equal(np.frombuffer(reply[1], dtype='<f4').reshape(frame.shape), expected)
    for mode in ("scan", "scan_inverted"):
        options = f"{base} --mode {mode}"
        zero, _ = smoke.render(exe, frame, options + " --print-exhaustion 0")
        changed, _ = smoke.render(exe, frame, options + " --print-exhaustion 1")
        np.testing.assert_array_equal(changed, zero, err_msg="exhaustion changed Scan")
    for value in ("-0.1", "1.1", "nan", "inf"):
        reply = smoke.run_stream(exe, smoke.frame_request(frame, base + f" --print-exhaustion {value}"))
        assert len(reply) == 1 and reply[0][0] == 1, (value, reply)
    print(f"PASS: {len(cases)} paper/gamma/push cases, zero parity, nonzero response, continuity, REUSE, Scan, bounds; max near-zero RGB delta={max_near_zero_delta:.9g}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    args = parser.parse_args()
    run(args.exe, args.baseline)
