"""Headless option audit: sweeps every renderer option the panel exposes on real C1 frames.

No Capture One window is used. Frames are float RGBA sources captured from copy-only C1
sessions (`*.source.f32`) plus a synthetic chart. Panel defaults and option ranges come from
tests/OptionWiringTests (defaults-*.json, ranges.json). For each option the script renders the
range with the option's prerequisites active and records image statistics, so direction,
monotonicity and inactive-mode behaviour can be compared with the Spektrafilm specification.

Usage:
  python option_audit.py --exe RENDERER --audit-dir artifacts/plugin-audit \
      --source frame.f32 --size 8514x5676 [--scale 3] --out results.json [--only key,key]
"""
import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "GrainMeanTests"))
from compare_renderers import Worker  # noqa: E402

# Look-level options (SpektraFilmTool), not in ranges.json: key -> (min, max, default).
LOOK_NUMERIC = {
    "film-exposure": (-8, 8, 0), "print-exposure": (-5, 5, 0),
    "film-gamma": (0.1, 2, 1), "film-push-pull": (-2, 2, 0), "negative-bleach-bypass": (0, 1, 0),
    "negative-leuco-cyan": (0, 2, 1), "uv-cutoff": (380, 450, 380), "ir-cutoff": (600, 780, 780),
    "print-gamma": (0.1, 2, 1), "print-push-pull": (-2, 2, 0), "print-shadow-shape": (-1, 1, 0),
    "print-highlight-shape": (-1, 1, 0), "print-exhaustion": (0, 1, 0), "print-bleach-bypass": (0, 1, 0),
    "cyan-filter": (0, 120, 0), "magenta-filter": (-60, 60, 0), "yellow-filter": (-60, 60, 0),
    "preflash": (0, 1, 0), "preflash-magenta": (-60, 60, 0), "preflash-yellow": (-60, 60, 0),
    "dir-amount": (0, 2, 1), "dir-same-layer": (0, 2, 1), "dir-interlayer": (0, 2, 1),
}
LOOK_CHOICES = {
    "mode": ["print", "scan", "scan_inverted"],
    "film-push-pull-mode": ["standard", "experimental"],
    "spectral-method": ["hanatos2025", "hanatos2026", "mallett2019"],
}
# Options enabled by a panel toggle the renderer never sees, or that are C1-local.
SKIP = {"export-jpeg-quality"}
INTEGER_OPTIONS = {"grain-seed", "grain-sublayer-count", "grain-synthesis-samples", "grain-synthesis-cell-cap"}
LOCAL_PREFIXES = ("finishing",)
CHANNEL_GROUPS = (
    ("halation-red", "halation-green", "halation-blue"),
    ("dir-gamma-r", "dir-gamma-g", "dir-gamma-b", "dir-r-to-g", "dir-r-to-b",
     "dir-g-to-r", "dir-g-to-b", "dir-b-to-r", "dir-b-to-g"),
)


def prerequisites(key: str) -> dict[str, str]:
    """Settings that must be active for `key` to be read by the renderer."""
    pre: dict[str, str] = {}
    if key.startswith("grain-synthesis") or key == "grain-synthesis-layered":
        pre["grain-model"] = "synthesis"; pre["grain-synthesis-samples"] = "16"
    elif key.startswith("grain-rms"):
        pre["grain-model"] = "rms"
    elif key.startswith("grain-") and key != "grain-model":
        pre["grain-model"] = "production"
    if key in ("film-age-grain",):
        pre["grain-model"] = "production"
    if key.startswith("halation-") and key != "halation-amount":
        pre["halation-amount"] = "2"
    if key in ("halation-boost-range", "halation-protect", "halation-protect-relative"):
        pre["halation-boost"] = "3"
    if key == "scatter-scale":
        pre["scatter-amount"] = "1"
    if key.startswith("camera-diffusion-") and key != "camera-diffusion-family":
        pre["camera-diffusion-family"] = "black_pro_mist"
    if key.startswith("print-diffusion-") and key != "print-diffusion-family":
        pre["print-diffusion-family"] = "black_pro_mist"
    if key.startswith("scanner-") and key != "scanner-enabled":
        pre["scanner-enabled"] = "true"
        if key.startswith("scanner-white"):
            pre["scanner-white"] = "true"
        if key.startswith("scanner-black"):
            pre["scanner-black"] = "true"
    if key.startswith("dir-tail") or key == "dir-diffusion":
        pre["dir-diffusion"] = "20"
    if key.startswith("film-age-") and key != "film-aging-enabled":
        pre["film-aging-enabled"] = "true"; pre["film-age-years"] = "10"
    if key.startswith("dir-") and ("-to-" in key or key.startswith("dir-gamma")):
        pre["dir-amount"] = "1"
    if key.startswith("enlarger-") and key != "enlarger-scale":
        pre["enlarger-scale"] = "1.5"
    if key.startswith("auto-exposure-"):
        pre["auto-exposure"] = "true"
    # Conditions the panel itself provides (SPEC.md section 0).
    if key == "uv-cutoff":
        pre["uv-enabled"] = "true"
    if key == "ir-cutoff":
        pre["ir-enabled"] = "true"
    if key == "negative-leuco-cyan":
        pre["negative-bleach-bypass"] = "1"
    if key in ("preflash-magenta", "preflash-yellow"):
        pre["preflash"] = "0.03"
    if key in ("grain-rms-r", "grain-rms-g", "grain-rms-b"):
        pre["grain-rms-stock"] = "false"
    if key == "grain-sublayer-count":
        pre["grain-sublayers"] = "false"
    if key == "film-format":
        pre["grain-model"] = "production"; pre["halation-amount"] = "2"
    if key == "film-push-pull-mode":
        pre["film-push-pull"] = "1"
    if key == "grain-synthesis-radius-quantile":
        pre["grain-synthesis-radius-sd"] = "0.2"
    if key in ("grain-microstructure", "grain-microstructure-sigma"):
        pre["film-format"] = "8"
    if key == "grain-microstructure":
        pre["grain-microstructure-sigma"] = "500"  # keep modulation above the shader's 0.05 px cutoff
    if key == "scanner-unsharp-amount":
        pre["scanner-unsharp-radius"] = "40"  # resolve the sharpening kernel even on reduced frames
    if key == "scanner-enabled":
        pre["scanner-mtf"] = "5"
    if key in ("film-base-enabled", "print-base-enabled"):
        pre[key.replace("enabled", "scale")] = "1.5"
    if key == "film-aging-enabled":
        pre["film-age-years"] = "10"
    return pre


def box_down(image: np.ndarray, factor: int) -> np.ndarray:
    if factor <= 1:
        return image
    h, w = (image.shape[0] // factor) * factor, (image.shape[1] // factor) * factor
    v = image[:h, :w].reshape(h // factor, factor, w // factor, factor, 4).mean(axis=(1, 3))
    return np.ascontiguousarray(v, dtype=np.float32)


def chart(width: int = 2400, height: int = 1600) -> np.ndarray:
    """Neutral ramp, colour patches, and small bright lights on a dark ground."""
    img = np.zeros((height, width, 4), np.float32); img[..., 3] = 1
    ramp = np.linspace(0, 1, width, dtype=np.float32) ** 1.8
    img[: height // 4, :, :3] = ramp[None, :, None]
    colours = [(0.8, 0.1, 0.1), (0.1, 0.7, 0.1), (0.1, 0.1, 0.8), (0.8, 0.8, 0.1), (0.1, 0.8, 0.8),
               (0.8, 0.1, 0.8), (0.6, 0.45, 0.35), (0.3, 0.4, 0.6), (0.18, 0.18, 0.18), (0.5, 0.5, 0.5)]
    pw = width // len(colours)
    for i, c in enumerate(colours):
        img[height // 4: height // 2, i * pw:(i + 1) * pw, :3] = c
    img[height // 2:, :, :3] = 0.03
    yy, xx = np.mgrid[0:height // 2, 0:width]
    for cx in range(width // 10, width, width // 5):
        mask = (xx - cx) ** 2 + (yy - height // 4) ** 2 < (height // 40) ** 2
        img[height // 2:, :, :3][mask] = 1.0
    return img


def blur(y: np.ndarray, radius: int) -> np.ndarray:
    k = 2 * radius + 1
    c = np.cumsum(np.pad(y, ((radius + 1, radius), (0, 0)), mode="edge"), axis=0)
    y = (c[k:] - c[:-k]) / k
    c = np.cumsum(np.pad(y, ((0, 0), (radius + 1, radius)), mode="edge"), axis=1)
    return (c[:, k:] - c[:, :-k]) / k


def stats(out: np.ndarray, base: np.ndarray | None, source_y: np.ndarray) -> dict[str, float]:
    rgb = out[..., :3].astype(np.float64)
    y = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]
    s = {
        "finite": float(np.isfinite(rgb).all()),
        "mean_r": float(rgb[..., 0].mean()), "mean_g": float(rgb[..., 1].mean()), "mean_b": float(rgb[..., 2].mean()),
        "y_mean": float(y.mean()), "y_p05": float(np.percentile(y, 5)), "y_p50": float(np.percentile(y, 50)),
        "y_p95": float(np.percentile(y, 95)),
        "contrast": float(np.percentile(y, 95) - np.percentile(y, 5)),
        "chroma": float((rgb.max(axis=2) - rgb.min(axis=2)).mean()),
        "warmth": float((rgb[..., 0] - rgb[..., 2]).mean()),
        "magenta": float((0.5 * (rgb[..., 0] + rgb[..., 2]) - rgb[..., 1]).mean()),
        "hf": float(np.std(y - blur(y, 1))),
        "glow": float(np.mean(blur(y, 24) - blur(y, 3))) if y.shape[0] > 60 else 0.0,
    }
    if base is not None:
        d = np.abs(rgb - base[..., :3].astype(np.float64)).mean(axis=2)
        s["diff_mean"] = float(d.mean()); s["diff_max"] = float(d.max())
        hi, lo = source_y > np.percentile(source_y, 90), source_y < np.percentile(source_y, 10)
        s["diff_highlights"] = float(d[hi].mean()); s["diff_shadows"] = float(d[lo].mean())
        # Light leaking out of highlights: change in the dark ring just outside bright areas.
        ring = (blur((source_y > np.percentile(source_y, 98)).astype(np.float64), 12) > 0.02) & (source_y < np.percentile(source_y, 60))
        s["diff_ring"] = float(d[ring].mean()) if ring.any() else 0.0
    return s


def values_for(key: str, spec: dict) -> list[str]:
    if "choices" in spec:
        return list(spec["choices"])
    if "toggle" in spec:
        return ["false", "true"]
    lo, hi, cur = spec["min"], spec["max"], spec["value"]
    vals = sorted({lo, lo + (hi - lo) * 0.25, cur, lo + (hi - lo) * 0.5, lo + (hi - lo) * 0.75, hi})
    if key == "scanner-mtf":
        vals = sorted(set(vals) | {5})  # 0 bypasses blur, 5 lp/mm produces a resolvable blur
    if key in INTEGER_OPTIONS:  # the panel rounds these (SpatialPreview.IntegerOptions)
        vals = sorted({round(v) for v in vals} - ({0} if key != "grain-seed" else set()))
        if key == "grain-seed":
            vals = [1, 7, 123456]
        return [str(int(v)) for v in vals]
    if key == "grain-synthesis-radius-sd":
        vals = [v for v in vals if v <= 0.3]  # larger values exceed the GPU watchdog (audit finding)
    return [f"{v:.6g}" for v in vals]


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--exe", required=True)
    p.add_argument("--audit-dir", required=True, type=Path)
    p.add_argument("--source")
    p.add_argument("--size")
    p.add_argument("--scale", type=int, default=3)
    p.add_argument("--chart", action="store_true")
    p.add_argument("--modes", default="print,scan")
    p.add_argument("--only")
    p.add_argument("--out", required=True, type=Path)
    p.add_argument("--stocks", type=Path, default=Path("stocks.json"))
    a = p.parse_args()
    if a.chart:
        frame = chart()
    else:
        w, h = map(int, a.size.split("x"))
        frame = box_down(np.fromfile(a.source, "<f4").reshape(h, w, 4), a.scale)
    src = frame[..., :3].astype(np.float64)
    source_y = 0.2126 * src[..., 0] + 0.7152 * src[..., 1] + 0.0722 * src[..., 2]
    ranges = json.loads((a.audit_dir / "ranges.json").read_text())
    for k, (lo, hi, cur) in LOOK_NUMERIC.items():
        ranges[k] = {"min": lo, "max": hi, "value": cur}
    for k, items in LOOK_CHOICES.items():
        ranges[k] = {"choices": items}
    catalog = json.loads(a.stocks.read_text(encoding="utf-8"))
    ranges["stock"] = {"choices": [f["Id"] for f in catalog["Films"]]}
    ranges["paper"] = {"choices": [f["Id"] for f in catalog["Papers"]]}
    kinds = {f["Id"]: f.get("Kind") for f in catalog["Films"]}
    only = set(a.only.split(",")) if a.only else None
    worker = Worker(a.exe, "--export-stream")
    results = {"frame": list(frame.shape[:2]), "options": []}
    started = time.time()
    try:
        for mode in a.modes.split(","):
            defaults_file = a.audit_dir / f"defaults-{'scan' if mode.startswith('scan') else 'print'}.json"
            base = {k[2:]: v for k, v in json.loads(defaults_file.read_text()).items()}
            base["mode"] = mode
            base["grain-seed"] = "7"
            # The panel always sends the look's development sliders (SpektraFilmTool.CurrentSettings).
            for k, (_, _, default) in LOOK_NUMERIC.items():
                base.setdefault(k, f"{default:g}")
            base.setdefault("film-push-pull-mode", "standard")
            for key, spec in sorted(ranges.items()):
                if key in SKIP or key.startswith(LOCAL_PREFIXES) or (only and key not in only):
                    continue
                if key == "mode":
                    continue
                # With the corresponding custom switch on, the panel sends every
                # slider in the group. Neutral arguments omit those keys entirely.
                channels = next((group for group in CHANNEL_GROUPS if key in group), ())
                if not channels and key not in base and key not in LOOK_NUMERIC and key not in LOOK_CHOICES:
                    results["options"].append({"mode": mode, "key": key, "note": "panel-only (not sent to renderer)"})
                    continue
                opts = dict(base); opts.update(prerequisites(key))
                opts.update({channel: str(ranges[channel]["value"]) for channel in channels})
                reference = worker.render(frame, opts)
                rows = []
                for value in values_for(key, spec):
                    if key == "stock" and mode == "print" and kinds.get(value) == "positive":
                        continue  # the panel offers reversal films only in Scan
                    o = dict(opts); o[key] = value
                    try:
                        out = worker.render(frame, o)
                        rows.append({"value": value, **stats(out, reference, source_y)})
                    except RuntimeError as error:
                        rows.append({"value": value, "error": str(error)[:300]})
                        worker.close(); worker = Worker(a.exe, "--export-stream")
                results["options"].append({"mode": mode, "key": key, "prerequisites": prerequisites(key),
                                           "baseline": opts.get(key), "rows": rows})
                print(f"{mode:13s} {key:40s} {len(rows)} renders ({time.time() - started:6.0f} s)", flush=True)
                a.out.write_text(json.dumps(results, indent=1))
    finally:
        worker.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
