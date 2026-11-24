"""Stage-by-stage comparison of the Vulkan still renderer with the Python reference (spektrafilm).

Both engines render the same synthetic ProPhoto chart (ROMM-encoded, as the panel sends it); the renderer returns each stage through its diagnostic
--debug-tap option, Python through its pipeline taps (process(collect=...)). Spatial and stochastic effects are off
on both sides (Python debug.deactivate_spatial_effects / deactivate_stochastic_effects), so every stage is a per-pixel
transform and should agree up to float32 precision and intended differences (listed in README.md next to this file).

Usage (Python with the reference's dependencies, e.g. artifacts/quality-audit-20261115/venv):
  python stage_parity.py <SpektraStillRenderer.exe> <upstream-spektrafilm/src> [--case NAME ...] [--json out.json]

Taps: film-log = log10 film exposure (Python log_e_film), film-density = CMY film density (cmy_film),
print-log = log10 print exposure (log_e_print), print-density = CMY print density (cmy_print), output = final RGB
(rgb_out, sRGB-encoded). Reported per tap: max, mean and 99th percentile of the absolute difference over all pixels
and channels; densities and logs in their own units, output in 8-bit code values (x255).
"""
import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import Worker  # noqa: E402

TAPS = {  # renderer tap name -> Python Tap attribute
    "film-log": "LOG_E_FILM",
    "film-density": "CMY_FILM",
    "print-log": "LOG_E_PRINT",
    "print-density": "CMY_PRINT",
    "output": "RGB_OUT",
}
PRINT_ONLY = {"print-log", "print-density"}


CHART_SIZE = (288, 192)


def chart(width: int = CHART_SIZE[0], height: int = CHART_SIZE[1]) -> "tuple[np.ndarray, dict]":
    """ProPhoto chart (values as ROMM-encoded, the way the panel sends them): a neutral ramp of 12 stops, a hue sweep at three saturations and three levels, pure
    primaries/secondaries at three levels, and a smooth 2D field. Also returns the row ranges of each region."""
    img = np.ones((height, width, 4), np.float32)
    regions = {}
    band = height // 6
    # neutral ramp 2^-9 .. 2^3 x 0.18
    x = np.linspace(-9, 3, width)
    img[:band, :, :3] = (0.18 * 2.0 ** x)[None, :, None]
    regions["neutral ramp"] = (0, band)
    # hue sweep (HSV-like in linear ProPhoto) at 3 saturations x 3 levels
    hue = np.linspace(0, 1, width, endpoint=False)
    def hsv(h, s, v):
        k = (np.array([5, 3, 1])[None, :] + h[:, None] * 6) % 6
        return v * (1 - s * np.clip(np.minimum(k, 4 - k), 0, 1))
    rows = band
    for s in (0.3, 0.7, 1.0):
        start = rows
        for v in (0.05, 0.2, 0.6):
            h = max(1, (3 * band) // 9)
            img[rows:rows + h, :, :3] = hsv(hue, s, v)[None, :, :]
            rows += h
        regions[f"hues, saturation {s}"] = (start, rows)
    # primaries and secondaries at three levels
    colours = np.array([[1, 0, 0], [0, 1, 0], [0, 0, 1], [0, 1, 1], [1, 0, 1], [1, 1, 0]], np.float32)
    cell = width // 18
    for i, c in enumerate(colours):
        for j, level in enumerate((0.03, 0.15, 0.6)):
            x0 = (i * 3 + j) * cell
            img[rows:rows + band, x0:x0 + cell, :3] = c * level
    regions["primaries and secondaries"] = (rows, rows + band)
    rows += band
    # smooth field
    yy, xx = np.mgrid[0:height - rows, 0:width].astype(np.float32)
    img[rows:, :, 0] = 0.02 + 0.5 * (xx / width) ** 2
    img[rows:, :, 1] = 0.02 + 0.4 * (yy / max(1, height - rows))
    img[rows:, :, 2] = 0.05 + 0.3 * (1 - xx / width)
    regions["smooth field"] = (rows, height)
    return img, regions


# Renderer options that switch off every spatial and stochastic effect and match Python's defaults where the two
# differ. Spectral method: Python "hanatos2025" with its default fitted adaptation window
# (settings.apply_hanatos2025_adaptation_window) is the renderer's "hanatos2026"; the renderer's "hanatos2025" is the
# archived bandpass (the panel's default, compared in the "*-panel-method" case). Colour adaptation: the renderer
# applies its input and output gamut compression only with --color-adaptation true (the panel sends true; the
# renderer default is false), Python has both on by default.
OURS_BASE = {
    "input-color": "prophoto", "output-color": "srgb", "spectral-method": "hanatos2026", "color-adaptation": "true",
    "auto-exposure": "false",
    "film-exposure": "0", "print-exposure": "0", "film-format": "35", "grain-model": "off", "halation-amount": "0",
    "scatter-amount": "0", "camera-diffusion-family": "off", "print-diffusion-family": "off", "dir-amount": "0",
    "dir-diffusion": "0", "scanner-enabled": "false",
}

CASES = {
    "portra400-endura-m1": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="1"),
    "portra400-endura-m2": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="2"),
    "portra400-endura-m2-panel-method": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print",
                                             model="2", ours={"spectral-method": "hanatos2025"}),
    "vision3-500t-endura-m2": dict(stock="kodak_vision3_500t", paper="kodak_portra_endura", mode="print", model="2"),
    "portra400-supra-m2": dict(stock="kodak_portra_400", paper="kodak_supra_endura", mode="print", model="2"),
    "portra400-endura-dir-m2": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="2", dir=True),
    "portra400-endura-ev-m2": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="2",
                                   film_ev=1.0, print_ev=0.5),
    "velvia100-scan-m2": dict(stock="fujifilm_velvia_100", paper="kodak_portra_endura", mode="scan", model="2"),
    "portra400-scan-m2": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="scan", model="2"),
    # Film model 3: the reference's stock profiles; output gamut compression still OkLch (the reference uses CAM16-UCS).
    "portra400-endura-m3": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="3"),
    "velvia100-scan-m3": dict(stock="fujifilm_velvia_100", paper="kodak_portra_endura", mode="scan", model="3"),
    # Film model 4: CAM16-UCS output gamut compression as the reference.
    "portra400-endura-m4": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="4"),
    "vision3-500t-endura-m4": dict(stock="kodak_vision3_500t", paper="kodak_portra_endura", mode="print", model="4"),
    "velvia100-scan-m4": dict(stock="fujifilm_velvia_100", paper="kodak_portra_endura", mode="scan", model="4"),
    "portra400-endura-dir-m3": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="3", dir=True),
    "portra400-endura-dir-m4": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="4", dir=True),
    "velvia100-scan-dir-m3": dict(stock="fujifilm_velvia_100", paper="kodak_portra_endura", mode="scan", model="3", dir=True),
    "velvia100-scan-dir-m4": dict(stock="fujifilm_velvia_100", paper="kodak_portra_endura", mode="scan", model="4", dir=True),
    # Scanner blur and unsharp mask (px, Python's default unsharp mask): model 4 compresses the output gamut before them, as Python does.
    "portra400-endura-scanner-m3": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="3",
                                        scanner=(0.7, 0.7, 0.7)),
    "portra400-endura-scanner-m4": dict(stock="kodak_portra_400", paper="kodak_portra_endura", mode="print", model="4",
                                        scanner=(0.7, 0.7, 0.7)),
    "velvia100-scan-scanner-m4": dict(stock="fujifilm_velvia_100", paper="kodak_portra_endura", mode="scan", model="4",
                                      scanner=(0.7, 0.7, 0.7)),
}


def python_params(case: dict):
    from spektrafilm.runtime.params_builder import digest_params, init_params
    p = init_params(film_profile=case["stock"], print_profile=case["paper"])
    p.camera.auto_exposure = False
    p.camera.exposure_compensation_ev = case.get("film_ev", 0.0)
    p.enlarger.print_exposure = 2.0 ** case.get("print_ev", 0.0)
    # The renderer times the print for a 0 EV mid grey (SpektraPrintScan.comp printMidgrayExposureFactor), so film
    # exposure also changes print brightness; Python by default re-times the print for the film EV. Intended and
    # documented (docs/PLUGIN-AUDIT-20261117.md, panel tooltip of Film exposure).
    p.enlarger.print_exposure_compensation = False
    p.io.input_color_space = "ProPhoto RGB"
    p.io.input_cctf_decoding = True  # the renderer's "prophoto" is ROMM-encoded ProPhoto, as the panel sends it
    p.io.output_color_space = "sRGB"
    p.io.output_cctf_encoding = True
    p.io.scan_film = case["mode"] == "scan"
    p.settings.rgb_to_raw_method = "hanatos2025"
    p.debug.deactivate_spatial_effects = True
    p.debug.deactivate_stochastic_effects = True
    p.film_render.dir_couplers.active = bool(case.get("dir"))
    digested = digest_params(p)
    if case.get("scanner"):  # after digest_params, which zeroes them for deactivate_spatial_effects
        lens_px, unsharp_px, amount = case["scanner"]
        digested.scanner.lens_blur = lens_px
        digested.scanner.unsharp_mask = (unsharp_px, amount)
    return digested


def our_options(case: dict, params) -> dict:
    o = dict(OURS_BASE, stock=case["stock"], paper=case["paper"], mode=case["mode"])
    if case["model"] != "1":
        o["film-model"] = case["model"]
    o["film-exposure"] = str(case.get("film_ev", 0.0))
    o["print-exposure"] = str(case.get("print_ev", 0.0))
    o.update(case.get("ours", {}))
    if case.get("scanner"):
        # The renderer takes the scanner blur as MTF50 and the unsharp radius in film um; film pixel = 35 mm / long edge.
        lens_px, unsharp_px, amount = case["scanner"]
        pixel_um = 35000.0 / max(CHART_SIZE)
        o.update({"scanner-enabled": "true", "scanner-glare": "0",
                  "scanner-mtf": str(1000.0 * math.sqrt(math.log(2.0) / (2.0 * math.pi ** 2)) / (lens_px * pixel_um)),
                  "scanner-unsharp-radius": str(unsharp_px * pixel_um), "scanner-unsharp-amount": str(amount)})
    if case.get("dir"):
        d = params.film_render.dir_couplers
        (gr, gg, gb), (r2g, r2b), (g2r, g2b), (b2r, b2g) = (d.gamma_samelayer_rgb, d.gamma_interlayer_r_to_gb,
                                                            d.gamma_interlayer_g_to_rb, d.gamma_interlayer_b_to_rg)
        o.update({"dir-amount": str(d.amount), "dir-same-layer": str(d.inhibition_samelayer),
                  "dir-interlayer": str(d.inhibition_interlayer), "dir-gamma-r": str(gr), "dir-gamma-g": str(gg),
                  "dir-gamma-b": str(gb), "dir-r-to-g": str(r2g), "dir-r-to-b": str(r2b), "dir-g-to-r": str(g2r),
                  "dir-g-to-b": str(g2b), "dir-b-to-r": str(b2r), "dir-b-to-g": str(b2g)})
    return o


def stats(ours: np.ndarray, ref: np.ndarray, scale: float, regions: dict) -> dict:
    """Absolute differences overall and per chart region (max and mean; signed mean per channel for the region)."""
    signed = (ours.astype(np.float64) - ref.astype(np.float64)) * scale
    d = np.abs(signed)
    finite = np.isfinite(d)
    out = {"max": float(d[finite].max()), "mean": float(d[finite].mean()), "p99": float(np.percentile(d[finite], 99)),
           "nonfinite": int((~finite).sum()), "regions": {}}
    for name, (r0, r1) in regions.items():
        part, sgn = d[r0:r1], signed[r0:r1]
        ok = np.isfinite(part).all(axis=-1)
        out["regions"][name] = {"max": float(part[ok].max()), "mean": float(part[ok].mean()),
                                "bias": [round(float(v), 5) for v in sgn[ok].mean(axis=0)]}
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("exe")
    ap.add_argument("reference_src")
    ap.add_argument("--case", action="append")
    ap.add_argument("--json")
    ap.add_argument("--regions", action="store_true", help="print the per-region breakdown")
    ap.add_argument("--sweep", metavar="MODEL", help="every film of the reference at this film model (negatives in "
                    "print on Portra Endura, slides in scan), one summary line each")
    a = ap.parse_args()
    sys.path.insert(0, a.reference_src)
    from spektrafilm.runtime.pipeline import SimulationPipeline
    from spektrafilm.runtime.topology import Tap

    img, regions = chart()
    worker = Worker(a.exe)
    report = {}
    if a.sweep:
        from spektrafilm.runtime.params_builder import init_params
        sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "upstream-ofx/tools"))
        from ofx_stock_lists import FILMS  # the renderer's films (papers excluded)
        films = sorted(FILMS)
        for stock in films:
            try:
                positive = init_params(film_profile=stock, print_profile="kodak_portra_endura").film.is_positive
            except Exception:
                continue  # print papers
            CASES[f"sweep-{stock}"] = dict(stock=stock, paper="kodak_portra_endura", mode="scan" if positive else "print",
                                           model=a.sweep)
        a.case = [name for name in CASES if name.startswith("sweep-")]
    try:
        for name in a.case or CASES:
            case = CASES[name]
            params = python_params(case)
            pipeline = SimulationPipeline(params)
            opts = our_options(case, params)
            report[name] = {}
            for tap, py_tap in TAPS.items():
                if tap in PRINT_ONLY and case["mode"] != "print":
                    continue
                ref = pipeline.process(img[..., :3].astype(np.float64), collect=getattr(Tap, py_tap))
                ours = worker.render(img, opts if tap == "output" else dict(opts, **{"debug-tap": tap}))[..., :3]
                report[name][tap] = stats(ours, ref, 255.0 if tap == "output" else 1.0, regions)
            print(name)
            for tap, s in report[name].items():
                unit = "x255" if tap == "output" else "    "
                print(f"  {tap:14s} {unit} max {s['max']:9.4f}  mean {s['mean']:9.5f}  p99 {s['p99']:9.5f}"
                      + (f"  non-finite {s['nonfinite']}" if s["nonfinite"] else ""))
                if a.regions:
                    for region, r in s["regions"].items():
                        print(f"      {region:28s} max {r['max']:9.4f}  mean {r['mean']:9.5f}  bias {r['bias']}")
    finally:
        worker.close()
    if a.json:
        Path(a.json).write_text(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
