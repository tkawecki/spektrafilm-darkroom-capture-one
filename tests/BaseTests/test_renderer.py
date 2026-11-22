"""Real Vulkan coverage of spectral film/paper base controls."""
import argparse
import importlib.util
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('smoke', ROOT / 'ofx-still-renderer/smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def run(exe, baseline):
    frame = smoke.source_frame(None)[::4, ::4].copy()
    base = '--input-color srgb --output-color srgb --grain-model off --auto-exposure 0'
    neutral = (' --film-base-enabled 1 --film-base-scale 1 --film-base-cyan 1'
               ' --film-base-magenta 1 --film-base-yellow 1 --film-base-tilt 0'
               ' --print-base-enabled 1 --print-base-scale 1 --print-base-cyan 1'
               ' --print-base-magenta 1 --print-base-yellow 1')
    for mode in ('print', 'scan', 'scan_inverted'):
        options = base + ' --mode ' + mode
        old, _ = smoke.render(baseline, frame, options)
        current, _ = smoke.render(exe, frame, options + neutral)
        np.testing.assert_array_equal(current, old)
        variants = ['--film-base-scale 1.5', '--film-base-cyan 1.5', '--film-base-magenta 1.5',
                    '--film-base-yellow 1.5', '--film-base-tilt 0.4', '--print-base-scale 2',
                    '--print-base-cyan 2', '--print-base-magenta 2', '--print-base-yellow 2']
        changed_options = [options + ' ' + variant for variant in variants]
        request = smoke.frame_request(frame, options)
        request += b''.join(f'REUSE {item}\n'.encode() for item in changed_options)
        request += f'REUSE {options}\nQUIT\n'.encode()
        replies = smoke.run_stream(exe, request)
        assert len(replies) == len(variants) + 3
        for variant, reply in zip(variants, replies[1:-2]):
            assert reply[0] == 0, reply
            output = np.frombuffer(reply[1], dtype='<f4').reshape(frame.shape)
            assert np.isfinite(output).all()
            np.testing.assert_array_equal(output[..., 3], frame[..., 3])
            delta = float(np.max(np.abs(output - current)))
            if mode != 'print' and variant.startswith('--print-base'):
                assert delta == 0, (mode, variant, delta)
            else:
                assert delta > 1e-6, (mode, variant, delta)
        np.testing.assert_array_equal(np.frombuffer(replies[-2][1], dtype='<f4').reshape(frame.shape), current)
        disabled, _ = smoke.render(exe, frame, options + ' --film-base-enabled 0 --film-base-scale 2 --film-base-cyan 2 --print-base-enabled 0 --print-base-scale 2 --print-base-yellow 2')
        np.testing.assert_array_equal(disabled, current)
    print('PASS: base controls, neutral parity, disabled parity, Print/Scan boundaries, REUSE, finite/alpha')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    args = parser.parse_args()
    run(args.exe, args.baseline)
