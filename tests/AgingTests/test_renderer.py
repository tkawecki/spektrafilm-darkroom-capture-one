"""Exercise native film aging without touching user sessions or photos."""
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
    base = '--input-color srgb --output-color srgb --auto-exposure 0 --grain-model off'
    for mode in ('print', 'scan', 'scan_inverted'):
        options = base + ' --mode ' + mode
        old, _ = smoke.render(baseline, frame, options)
        neutral, _ = smoke.render(exe, frame, options + ' --film-aging-enabled 1')
        np.testing.assert_array_equal(neutral, old)
        disabled, _ = smoke.render(exe, frame, options + ' --film-aging-enabled 0 --film-age-years 40 --film-age-fog-b 0.4')
        np.testing.assert_array_equal(disabled, old)
        variants = ['--film-age-years 30', '--film-age-fog-r 0.25', '--film-age-fog-g 0.25',
                    '--film-age-fog-b 0.25', '--film-age-gamma 0.6', '--film-age-gamma-r 0.6',
                    '--film-age-gamma-g 0.6', '--film-age-gamma-b 0.6', '--film-age-density 0.6',
                    '--film-age-speed-ev 2']
        request = smoke.frame_request(frame, options)
        request += b''.join(f'REUSE {options} --film-aging-enabled 1 {variant}\n'.encode() for variant in variants)
        request += f'REUSE {options}\nQUIT\n'.encode()
        replies = smoke.run_stream(exe, request)
        for variant, reply in zip(variants, replies[1:-2]):
            assert reply[0] == 0, reply
            output = np.frombuffer(reply[1], dtype='<f4').reshape(frame.shape)
            assert np.isfinite(output).all()
            np.testing.assert_array_equal(output[..., 3], frame[..., 3])
            assert np.max(np.abs(output - neutral)) > 1e-6, (mode, variant)
        np.testing.assert_array_equal(np.frombuffer(replies[-2][1], dtype='<f4').reshape(frame.shape), neutral)
    invalid = ['--film-age-years -1', '--film-age-years 101', '--film-age-storage missing',
               '--film-age-gamma 0', '--film-age-density 0', '--film-age-grain 9', '--film-age-speed-ev nan']
    replies = smoke.run_stream(exe, b''.join(smoke.frame_request(frame, base + ' ' + item) for item in invalid))
    assert len(replies) == len(invalid) and all(reply[0] == 1 for reply in replies)
    print('PASS: aging axes, neutral/disabled legacy parity, Print/Scan modes, REUSE, finite pixels and alpha')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    args = parser.parse_args()
    run(args.exe, args.baseline)
