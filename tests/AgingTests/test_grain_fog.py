"""Check fog is not counted twice and aging coarsens Production/RMS grain."""
import argparse
import importlib.util
import json
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('smoke', ROOT / 'ofx-still-renderer/smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def pair(exe, frame, options, change):
    request = smoke.frame_request(frame, options) + f'REUSE {options} {change}\nQUIT\n'.encode()
    replies = smoke.run_stream(exe, request)
    assert len(replies) == 3 and all(reply[0] == 0 for reply in replies)
    return [np.frombuffer(reply[1], dtype='<f4').reshape(frame.shape)[..., :3] for reply in replies[:2]]


def run(exe):
    frame = np.ones((64,128,4), np.float32)
    for i, value in enumerate((.01,.18,.5,.9)):
        frame[:,i*32:(i+1)*32,:3] = value
    base = ('--input-color srgb --output-color linear_rec709 --auto-exposure 0 --grain-microstructure 0'
            ' --grain-particle-area 0.01 --grain-final-blur 0 --grain-dye-cloud-blur 0'
            ' --grain-density-min-r 0 --grain-density-min-g 0 --grain-density-min-b 0')
    fog = '--film-aging-enabled 1 --film-age-fog-r 0.1 --film-age-fog-g 0.15 --film-age-fog-b 0.25'
    errors = {}
    for mode in ('scan', 'print'):
        deltas = []
        for grain in ('off', 'production', 'rms'):
            before, after = pair(exe, frame, f'{base} --mode {mode} --grain-model {grain}', fog)
            deltas.append((after-before).reshape(64,4,32,3).mean(axis=(0,2)))
        for i, grain in enumerate(('production','rms'), 1):
            error = float(np.max(np.abs(deltas[i] - deltas[0])))
            assert error < 0.001, (mode, grain, error, 'fog differs from grain-off; check layer consistency')
            errors[f'{mode}_{grain}'] = error
    flat = np.full((128,128,4), .18, np.float32)
    flat[...,3] = 1
    base = ('--input-color srgb --output-color linear_rec709 --mode scan --auto-exposure 0 --film-format 8'
            ' --grain-microstructure 0 --grain-particle-area 1 --grain-final-blur 0 --grain-dye-cloud-blur 0'
            ' --grain-rms-stock 0 --grain-rms-r 20 --grain-rms-g 20 --grain-rms-b 20')
    gains = {}
    for grain in ('production','rms'):
        before, after = pair(exe, flat, f'{base} --grain-model {grain}', '--film-aging-enabled 1 --film-age-grain 4')
        ratio = after.std(axis=(0,1)) / before.std(axis=(0,1))
        assert np.all((ratio > 1.7) & (ratio < 2.3)), (grain, ratio, 'quadrupling area should approximately double grain standard deviation')
        mean_change = float(np.max(np.abs(after.mean(axis=(0,1)) - before.mean(axis=(0,1)))))
        assert mean_change < 0.005, (grain, mean_change)
        gains[grain] = {'std_ratio': ratio.tolist(), 'max_mean_change': mean_change}
    print(json.dumps({'fog_grain_off_difference': errors, 'area_gain_4': gains}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, required=True)
    run(parser.parse_args().exe)
