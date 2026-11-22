"""Summarise an option_audit.py sweep: errors, inert options, unexpected effects, monotonicity.

Usage: python analyze.py sweep.json [--markdown out.md]
"""
import argparse
import json
from pathlib import Path

# Options the renderer ignores outside Print (SPEC.md section 0).
PRINT_ONLY = {
    "paper", "print-exposure", "print-gamma", "print-push-pull", "print-shadow-shape", "print-highlight-shape",
    "print-exhaustion", "print-bleach-bypass", "cyan-filter", "magenta-filter", "yellow-filter", "preflash",
    "preflash-magenta", "preflash-yellow", "scanner-glare", "scanner-glare-roughness", "scanner-glare-blur",
}
PRINT_ONLY_PREFIXES = ("print-diffusion-", "print-base-")
# Expected inert under the audit's prerequisites (documented upstream behaviour).
EXPECTED_INERT = {
    ("print", "grain-seed"): "pattern only; statistics unchanged",
    ("scan", "grain-seed"): "pattern only; statistics unchanged",
    ("print", "film-aging-enabled"): "neutral aging sliders are an identity",
    ("scan", "film-aging-enabled"): "neutral aging sliders are an identity",
    ("print", "film-base-enabled"): "neutral base sliders are an identity",
    ("scan", "film-base-enabled"): "neutral base sliders are an identity",
    ("print", "print-base-enabled"): "neutral base sliders are an identity",
    ("scan", "color-output-lightness-compression"): "acts only on out-of-range output; a negative scan stays in range",
    ("print", "grain-microstructure-sigma"): "sub-pixel; documented in the panel tooltip",
    ("scan", "grain-microstructure-sigma"): "sub-pixel; documented in the panel tooltip",
}
# Options that change framing, so image statistics need not be monotonic.
CONTENT_CHANGING = {"enlarger-scale", "enlarger-x", "enlarger-y"}
METRICS = ["y_mean", "y_p05", "y_p95", "contrast", "chroma", "warmth", "magenta", "hf", "glow"]


def numeric(values: list[str]) -> bool:
    try:
        [float(v) for v in values]
        return True
    except ValueError:
        return False


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("sweep", type=Path)
    p.add_argument("--markdown", type=Path)
    a = p.parse_args()
    data = json.loads(a.sweep.read_text())
    lines, flags = [], []
    for item in data["options"]:
        mode, key = item["mode"], item["key"]
        if "rows" not in item:
            continue
        rows = item["rows"]
        errors = [r for r in rows if "error" in r]
        ok = [r for r in rows if "error" not in r]
        if errors:
            flags.append(f"ERROR {mode} {key}: {errors[0]['error'][:160]}")
        if any(r.get("finite", 1) < 1 for r in ok):
            flags.append(f"NONFINITE {mode} {key}")
        effect = max((r.get("diff_mean", 0) for r in ok), default=0)
        print_only = key in PRINT_ONLY or key.startswith(PRINT_ONLY_PREFIXES)
        if mode.startswith("scan") and print_only:
            if effect > 1e-7:
                flags.append(f"SCAN-ACTIVE {mode} {key}: print-only option changes Scan output (diff {effect:.2e})")
        elif effect <= 1e-7 and (mode, key) not in EXPECTED_INERT:
            flags.append(f"INERT {mode} {key}: no pixel change across {[r['value'] for r in ok]} "
                         f"(prerequisites {item.get('prerequisites')})")
        best = ""
        if ok and numeric([r["value"] for r in ok]) and len(ok) >= 3:
            ordered = sorted(ok, key=lambda r: float(r["value"]))
            spans = {m: abs(ordered[-1][m] - ordered[0][m]) for m in METRICS if m in ordered[0]}
            best = max(spans, key=spans.get)
            series = [r[best] for r in ordered]
            steps = [b - c for c, b in zip(series, series[1:])]
            big = max(abs(s) for s in steps) or 1
            signs = {1 if s > 0 else -1 for s in steps if abs(s) > 0.05 * big}
            if len(signs) > 1 and effect > 1e-4 and key not in CONTENT_CHANGING:
                flags.append(f"NONMONOTONIC {mode} {key}: {best} = " + ", ".join(f"{r['value']}:{r[best]:.4f}" for r in ordered))
        lines.append(f"| {mode} | {key} | {len(ok)}/{len(rows)} | {effect:.2e} | {best} |")
    report = "\n".join(flags)
    print(report or "no flags")
    print(f"{len(lines)} option sweeps, {len(flags)} flags")
    if a.markdown:
        a.markdown.write_text("# Flags\n\n" + "\n".join(f"- {f}" for f in flags) +
                              "\n\n# Sweeps\n\n| mode | option | renders | max mean diff | main metric |\n|---|---|---|---|---|\n" + "\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
