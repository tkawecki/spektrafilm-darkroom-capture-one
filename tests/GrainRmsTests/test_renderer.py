"""RMS Vulkan integration and bit-exact legacy grain regression (no C1)."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/ProfileTests"))
sys.path.insert(0, str(ROOT / "upstream-ofx/tools"))
from profile_expansion import render_set, source_frame
from ofx_stock_lists import FILMS, _LEGACY_FILM_ORDER


def digest(frame):
    return hashlib.sha256(frame.tobytes()).hexdigest()


def run(baseline, candidate, reference, output):
    frame = source_frame(None)[::4, ::4].copy()
    base = "--paper kodak_portra_endura --input-color srgb --output-color srgb --spectral-method hanatos2025 --halation-amount 0 --scatter-amount 0 --auto-exposure 0 --color-adaptation 0"
    legacy = [(stock, mode, model) for stock in _LEGACY_FILM_ORDER
              for mode in ("print", "scan") for model in ("off", "preview", "production", "synthesis")]
    old_options = [f"{base} --stock {s} --mode {mode} --grain-model {model}" for s, mode, model in legacy]
    before = render_set(baseline, frame, old_options)
    print("Legacy grain baseline rendered", flush=True)
    after = render_set(candidate, frame, old_options)
    for label, a, b in zip(legacy, before, after):
        np.testing.assert_array_equal(a, b, err_msg=str(label))
    rms = [(stock, mode, layered) for stock in FILMS for mode in ("print", "scan") for layered in (0, 1)]
    rms_frames = render_set(candidate, frame, [f"{base} --stock {s} --mode {mode} --grain-model rms --grain-sublayers {layered}"
                                                   for s, mode, layered in rms])
    print("Stock RMS renders finite", flush=True)
    # Directly resolved RMS must be identical to Production fed the SAME areas
    # when no historical numerical floor is reached.
    areas = next(case["areas_um2"] for case in json.loads(reference.read_text(encoding="utf-8"))["results"]
                 if case["stock"] == "kodak_portra_400" and case["layers"] and not case["manual"])
    manual = " ".join(f"--grain-particle-scale-{ch} {area:.17g}" for ch, area in zip("rgb", areas))
    control = base + " --stock kodak_portra_400 --mode scan --grain-final-blur 0 --grain-dye-cloud-blur 0 --grain-microstructure 0"
    controls = [
        control + " --grain-model rms",
        control + " --grain-model rms --film-aging-enabled true --film-age-grain 4",
        control + " --grain-model production --grain-particle-area 1 " + manual,
        control + " --grain-model rms --grain-rms-stock false --grain-rms-r 4.5 --grain-rms-g 4.5 --grain-rms-b 4.5",
        control + " --grain-model rms --grain-rms-stock true --grain-rms-r 99 --grain-rms-g 99 --grain-rms-b 99",
        control + " --grain-model rms --grain-seed 17",
        control + " --grain-model rms --grain-rms-strength 2",
        control + " --grain-model rms --grain-rms-strength 0.001",
        control + " --grain-model rms --grain-rms-strength 0.002",
        control + " --grain-model rms --grain-amount 0",
        control + " --grain-model off",
        control + " --grain-model rms",
    ]
    rendered = render_set(candidate, frame, controls)
    for index in (2, 3, 4, 11):
        np.testing.assert_array_equal(rendered[0], rendered[index], err_msg=f"RMS equivalence control {index}")
    for index in (5, 6, 7):
        assert not np.array_equal(rendered[0], rendered[index]), controls[index]
    assert not np.array_equal(rendered[7], rendered[8]), "Low RMS strength was clamped to historical area floor"
    np.testing.assert_array_equal(rendered[9], rendered[10], err_msg="Grain amount zero")
    np.testing.assert_array_equal(rendered[1], rendered[6], err_msg="Aging grain gain4 must equal RMS strength2")
    # Explicitly compare the production export tiler with the full-frame path.
    # Physical film coordinates and overlap must be preserved for the new enum.
    tile_keys = ("SPEKTRAFILM_VULKAN_TILE_MODE", "SPEKTRAFILM_VULKAN_TILE_WIDTH", "SPEKTRAFILM_VULKAN_TILE_HEIGHT")
    saved = {key: os.environ.get(key) for key in tile_keys}
    tile_options = [base + f" --stock kodak_portra_400 --mode {mode} --grain-model rms --scanner-mtf 0 --scanner-unsharp-amount 0"
                    for mode in ("print", "scan")]
    try:
        os.environ[tile_keys[0]] = "full-frame"
        full_frames = render_set(candidate, source_frame(None), tile_options)
        os.environ[tile_keys[0]] = "tiled"
        os.environ[tile_keys[1]] = os.environ[tile_keys[2]] = "64"
        tile_frames = render_set(candidate, source_frame(None), tile_options)
    finally:
        for key, value in saved.items():
            if value is None: os.environ.pop(key, None)
            else: os.environ[key] = value
    for full, tile in zip(full_frames, tile_frames):
        np.testing.assert_array_equal(full, tile, err_msg="RMS tile/full-frame parity")
    report = {"baseline": str(baseline), "candidate": str(candidate), "legacy_bit_exact": len(legacy),
              "stock_rms_finite": len(rms), "control_cases": len(controls), "tile_parity_cases": len(tile_options),
              "legacy": [dict(stock=s, mode=m, grain=g, sha256=digest(f)) for (s, m, g), f in zip(legacy, after)],
              "rms": [dict(stock=s, mode=m, layers=l, sha256=digest(f)) for (s, m, l), f in zip(rms, rms_frames)],
              "controls": [dict(options=o, sha256=digest(f)) for o, f in zip(controls, rendered)]}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"PASS: {len(legacy)} legacy renders bit-exact; {len(rms)} RMS renders finite; {len(controls)} equivalence/control cases")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    run(args.baseline.resolve(), args.candidate.resolve(), args.reference, args.output)
