"""Compare option behaviour of the native renderer with the Python spektrafilm reference.

For options that exist in both implementations, both engines render the same C1 frame
(downscaled) at several values. The script compares the direction and size of the change of
simple image statistics. It does not demand pixel identity: Native follows the OFX port.
Spatial and stochastic effects are disabled in both, except for halation and scatter.

Run with a Python that has numpy, scipy, colour-science and scikit-image, e.g.
  <python> -I tests/OptionAudit/python_reference.py --exe RENDERER --source frame.f32 \
      --size 8514x5676 --out results.json
"""
import argparse
import copy
import json
import sys
from dataclasses import replace
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "upstream-spektrafilm" / "src"))
sys.path.insert(0, str(REPO / "tests" / "GrainMeanTests"))
from compare_renderers import Worker  # noqa: E402
from spektrafilm import init_params, simulate  # noqa: E402

NATIVE_BASE = {
    "stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print", "input-color": "prophoto",
    "output-color": "srgb", "spectral-method": "hanatos2025", "film-exposure": "0", "print-exposure": "0",
    "film-gamma": "1", "print-gamma": "1", "dir-amount": "1", "dir-same-layer": "1", "dir-interlayer": "1",
    "dir-diffusion": "0", "halation-amount": "0", "scatter-amount": "0", "grain-model": "off",
    "scanner-enabled": "false", "auto-exposure": "false", "color-adaptation": "false",
}


def py_params(stock: str, spatial: bool):
    p = init_params(stock, "kodak_portra_endura")
    p.io.input_color_space = "ProPhoto RGB"; p.io.input_cctf_decoding = True
    p.io.output_color_space = "sRGB"; p.io.output_cctf_encoding = True
    p.camera.auto_exposure = False
    p.enlarger.print_exposure_compensation = False  # OFX/native semantics, see SPEC.md 1
    p.debug.deactivate_stochastic_effects = True
    p.debug.deactivate_spatial_effects = not spatial
    p.film_render.grain.active = False
    p.film_render.glare.active = False; p.print_render.glare.active = False
    p.film_render.halation.active = spatial
    p.film_render.halation.halation_amount = 0.0; p.film_render.halation.scatter_amount = 0.0
    p.film_render.dir_couplers.diffusion_size_um = 0.0
    p.scanner.unsharp_mask = (0.0, 0.0)
    p.io.input_gamut_compress = replace(p.io.input_gamut_compress, active=False)
    p.io.output_gamut_compress = replace(p.io.output_gamut_compress, algorithm="off")
    p.settings.apply_hanatos2025_adaptation_window = False
    p.settings.apply_hanatos2025_adaptation_surface = False
    return p


def set_attr(p, path: str, value) -> None:
    """Set a dotted attribute; frozen dataclasses on the way are rebuilt with replace()."""
    def assign(obj, parts):
        if len(parts) == 1:
            new_value = value
        else:
            new_value = assign(getattr(obj, parts[0]), parts[1:])
        try:
            setattr(obj, parts[0], new_value)
            return obj
        except Exception:
            return replace(obj, **{parts[0]: new_value})
    assign(p, path.split("."))


# native key -> (values, python attribute path, value converter, spatial)
CASES = {
    "film-exposure": ([-2, 0, 2], "camera.exposure_compensation_ev", float, False),
    "print-exposure": ([-1, 0, 1], "enlarger.print_exposure", lambda v: 2.0 ** v, False),
    "film-gamma": ([0.7, 1, 1.3], "film_render.density_curve_gamma", float, False),
    "print-gamma": ([0.7, 1, 1.3], "print_render.density_curves_morph.gamma_factor", float, False),
    "print-exhaustion": ([0, 0.5, 1], "print_render.density_curves_morph.developer_exhaustion", float, False),
    "magenta-filter": ([-20, 0, 20], "enlarger.m_filter_shift", float, False),
    "yellow-filter": ([-20, 0, 20], "enlarger.y_filter_shift", float, False),
    "cyan-filter": ([0, 20, 40], "enlarger.c_filter_neutral", float, False),
    "preflash": ([0, 0.02, 0.05], "enlarger.preflash_exposure", float, False),
    "dir-amount": ([0, 1, 2], "film_render.dir_couplers.amount", float, False),
    "dir-interlayer": ([0, 1, 2], "film_render.dir_couplers.inhibition_interlayer", float, False),
    "dir-same-layer": ([0, 1, 2], "film_render.dir_couplers.inhibition_samelayer", float, False),
    "halation-amount": ([0, 2, 4], "film_render.halation.halation_amount", float, True),
    "scatter-amount": ([0, 1, 2], "film_render.halation.scatter_amount", float, True),
    "scanner-white-level": ([0.9, 0.95, 1.0], "scanner.white_level", float, False),
    "scanner-black-level": ([0.0, 0.03, 0.06], "scanner.black_level", float, False),
}
# Extra settings per case: (native options, python attribute assignments).
SCANNER_NATIVE = {"scanner-enabled": "true", "scanner-mtf": "0", "scanner-unsharp-amount": "0", "scanner-glare": "0"}
EXTRA = {
    "scanner-white-level": ({**SCANNER_NATIVE, "scanner-white": "true"}, [("scanner.white_correction", True)]),
    "scanner-black-level": ({**SCANNER_NATIVE, "scanner-black": "true"}, [("scanner.black_correction", True)]),
}


def stats(rgb: np.ndarray) -> dict[str, float]:
    rgb = np.clip(rgb.astype(np.float64), 0, 1)
    y = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]
    return {"y_mean": float(y.mean()), "contrast": float(np.percentile(y, 95) - np.percentile(y, 5)),
            "chroma": float((rgb.max(axis=2) - rgb.min(axis=2)).mean()),
            "warmth": float((rgb[..., 0] - rgb[..., 2]).mean()),
            "green": float((rgb[..., 1] - 0.5 * (rgb[..., 0] + rgb[..., 2])).mean()),
            "p95": float(np.percentile(y, 95)), "p05": float(np.percentile(y, 5))}


def main() -> int:
    from skimage.transform import resize
    a = argparse.ArgumentParser()
    a.add_argument("--exe", required=True); a.add_argument("--source", required=True)
    a.add_argument("--size", required=True); a.add_argument("--width", type=int, default=960)
    a.add_argument("--out", required=True, type=Path); a.add_argument("--only")
    args = a.parse_args()
    w, h = map(int, args.size.split("x"))
    full = np.fromfile(args.source, "<f4").reshape(h, w, 4)[..., :3]
    out_h = round(h * args.width / w)
    src = resize(full, (out_h, args.width, 3), anti_aliasing=True, preserve_range=True).astype(np.float32)
    rgba = np.ones((out_h, args.width, 4), np.float32); rgba[..., :3] = src
    worker = Worker(args.exe, "--export-stream")
    results = []
    try:
        for key, (values, path, convert, spatial) in CASES.items():
            if args.only and key not in args.only.split(","):
                continue
            rows = []
            for v in values:
                p = py_params("kodak_portra_400", spatial)
                if path.startswith("print_render.density_curves_morph"):
                    set_attr(p, "print_render.density_curves_morph.active", True)  # init_params leaves it off
                if key == "cyan-filter":
                    # digest_params replaces the neutral filters from its database; native adds the shift.
                    from spektrafilm.runtime.params_builder import apply_database_neutral_print_filters
                    apply_database_neutral_print_filters(p)
                    p.settings.neutral_print_filters_from_database = False
                    set_attr(p, path, p.enlarger.c_filter_neutral + convert(v))
                else:
                    set_attr(p, path, convert(v))
                for extra_path, extra_value in EXTRA.get(key, ({}, []))[1]:
                    set_attr(p, extra_path, extra_value)
                py = simulate(src, copy.deepcopy(p))
                opts = dict(NATIVE_BASE); opts.update(EXTRA.get(key, ({}, []))[0]); opts[key] = str(v)
                native = worker.render(rgba, opts)[..., :3]
                rows.append({"value": v, "python": stats(py), "native": stats(native),
                             "mean_abs_255": float(np.abs(np.clip(py, 0, 1) - np.clip(native, 0, 1)).mean() * 255)})
            # Direction agreement on the metric that moves most in Python.
            spans = {m: rows[-1]["python"][m] - rows[0]["python"][m] for m in rows[0]["python"]}
            metric = max(spans, key=lambda m: abs(spans[m]))
            native_span = rows[-1]["native"][metric] - rows[0]["native"][metric]
            agree = (spans[metric] > 0) == (native_span > 0) or abs(spans[metric]) < 1e-4
            ratio = native_span / spans[metric] if abs(spans[metric]) > 1e-6 else float("nan")
            results.append({"option": key, "metric": metric, "python_span": spans[metric], "native_span": native_span,
                            "direction_agrees": bool(agree), "native_to_python": ratio, "rows": rows})
            print(f"{key:18s} {metric:9s} python {spans[metric]:+.4f}  native {native_span:+.4f}  "
                  f"{'same direction' if agree else 'OPPOSITE'}  ratio {ratio:.2f}  "
                  f"mean |py-native| {np.mean([r['mean_abs_255'] for r in rows]):.1f}/255", flush=True)
            args.out.write_text(json.dumps(results, indent=1))
    finally:
        worker.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
