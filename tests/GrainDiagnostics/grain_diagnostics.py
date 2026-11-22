"""Observe the Vulkan Boolean grain model in density/coverage space.

This is a diagnostic of the existing model, not a tonal-neutrality acceptance test.
Only copied shader resources inside a newly-created workspace are instrumented.
It does not launch Capture One or access any document, photograph, or user look.
"""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time

import numpy as np


REPO = Path(__file__).resolve().parents[2]
BASE_OPTIONS = {
    "stock": "kodak_portra_400", "paper": "kodak_portra_endura", "mode": "print",
    "input-color": "srgb", "output-color": "srgb", "spectral-method": "hanatos2025",
    "dir-amount": 0, "dir-diffusion": 0, "halation-amount": 0, "scatter-amount": 0,
    "auto-exposure": "false", "color-adaptation": "false", "scanner-enabled": "false",
    "grain-model": "synthesis", "grain-final-blur": 0, "grain-dye-cloud-blur": 0,
    "grain-microstructure-sigma": 0,
}
DENSITY_RETURN = "return -log(max(1.0 - coverage, epsilon)) / 2.302585093;"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def exact(pipe, size: int) -> bytearray:
    result = bytearray(size)
    position = 0
    while position < size:
        count = pipe.readinto(memoryview(result)[position:])
        if not count:
            raise RuntimeError("Renderer closed before completing its response")
        position += count
    return result


def render(rgb: np.ndarray, options: dict, exe: Path, log: Path) -> np.ndarray:
    height, width = rgb.shape[:2]
    rgba = np.ones((height, width, 4), dtype="<f4")
    rgba[..., :3] = rgb
    env = {k: v for k, v in os.environ.items()
           if not k.startswith("SPEKTRAFILM_") and k != "DOTNET_STARTUP_HOOKS"}
    env.update(SPEKTRAFILM_VULKAN_TILE_WIDTH="2048", SPEKTRAFILM_VULKAN_TILE_HEIGHT="2048")
    with log.open("ab") as stderr:
        worker = subprocess.Popen([str(exe), "--native-preview-stream"], cwd=exe.parent,
                                  env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=stderr, bufsize=0)
        try:
            header = f"FRAME {width} {height} " + " ".join(
                f"--{key} {value}" for key, value in (BASE_OPTIONS | options).items()) + "\n"
            worker.stdin.write(header.encode("ascii"))
            worker.stdin.write(rgba.tobytes())
            worker.stdin.flush()
            code, size = struct.unpack("<II", exact(worker.stdout, 8))
            payload = exact(worker.stdout, size)
            if code:
                raise RuntimeError(payload.decode("utf-8", errors="replace"))
            result = np.frombuffer(payload, "<f4").reshape(height, width, 4)[..., :3].copy()
            worker.stdin.close()
            worker.wait(timeout=30)
            if worker.returncode or not np.isfinite(result).all():
                raise RuntimeError("Nonfinite output or unsuccessful renderer exit")
            return result
        finally:
            if worker.poll() is None:
                worker.kill()
                worker.wait()


def replace_once(text: str, needle: str, replacement: str) -> str:
    if text.count(needle) != 1:
        raise RuntimeError(f"Instrumentation anchor changed: {needle}")
    return text.replace(needle, replacement)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--renderer", type=Path, default=REPO / "build/full-export/Release/SpektraStillRenderer.exe")
    parser.add_argument("--glslang", type=Path, required=True, help="glslang.exe or glslangValidator.exe")
    parser.add_argument("--work", type=Path, default=REPO / "artifacts" / ("grain-diagnostics-" + datetime.now().strftime("%Y%m%d-%H%M%S")))
    args = parser.parse_args()
    renderer, compiler, work = args.renderer.resolve(), args.glslang.resolve(), args.work.resolve()
    # Refuse to instrument an existing runtime or any directory outside ignored artifacts.
    if not work.is_relative_to((REPO / "artifacts").resolve()) or work.exists():
        parser.error("--work must be a new directory inside this repository's artifacts directory")
    if not renderer.is_file() or not compiler.is_file():
        parser.error("renderer and glslang must be existing files")
    if work.is_relative_to(renderer.parent):
        parser.error("--work must not be inside the renderer directory being copied")

    shader_dir = REPO / "upstream-ofx/shaders/vulkan"
    grain_path, print_path = shader_dir / "SpektraGrain.comp", shader_dir / "SpektraPrintScan.comp"
    watched = [renderer, grain_path, print_path,
               renderer.parent / "shaders/SpektraGrain.comp.spv",
               renderer.parent / "shaders/SpektraPrintScan.comp.spv"]
    before = {str(path): sha256(path) for path in watched}
    work.mkdir(parents=True)
    shutil.copytree(renderer.parent, work / "runtime")
    exe = work / "runtime" / renderer.name
    grain = grain_path.read_text(encoding="utf-8")
    print_shader = print_path.read_text(encoding="utf-8")
    result = {
        "purpose": "Root-cause diagnosis; PASS does not certify tonal neutrality or scientific parity",
        "input": "synthetic flat neutral RGB patches; not a photograph",
        "sourceHashesBefore": before,
        "compilerHash": sha256(compiler),
        "compilerVersion": subprocess.check_output([str(compiler), "--version"], text=True),
        "baseOptions": BASE_OPTIONS, "cases": {},
        "references": ["https://spektrafilm.114c.de/technical/",
                       "https://www.ipol.im/pub/art/2017/192/"],
    }

    def compile_shader(name: str, code: str) -> None:
        source = work / (name + ".comp")
        source.write_text(code, encoding="utf-8")
        with (work / "compiler.log").open("ab") as log:
            subprocess.run([str(compiler), "-V", str(source), "-o",
                            str(work / "runtime/shaders" / (name + ".comp.spv"))],
                           check=True, stdout=log, stderr=log)

    def run(name: str, source: np.ndarray, options: dict) -> np.ndarray:
        started = time.perf_counter()
        out = render(source, options, exe, work / "renderer.log")
        np.save(work / (name + ".npy"), out)
        coverage_readback = name.startswith("coverage-")
        stats = {"options": options, "seconds": time.perf_counter() - started,
                 "measurement": "Boolean coverage" if coverage_readback else "CMY optical density",
                 "width": source.shape[1], "height": source.shape[0],
                 "meanDensity": out.mean((0, 1), dtype=np.float64).tolist(),
                 "stdDensity": out.std((0, 1), dtype=np.float64).tolist(),
                 "maximum": out.max((0, 1)).tolist()}
        if not coverage_readback:
            stats["meanTransmission"] = np.power(10., -out).mean((0, 1), dtype=np.float64).tolist()
        if source.shape[1] == 512:
            stats["patchMeans"] = [out[8:-8, i * 64 + 8:(i + 1) * 64 - 8].mean((0, 1), dtype=np.float64).tolist()
                                   for i in range(8)]
        result["cases"][name] = stats
        (work / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(name, f"{stats['seconds']:.2f}s", flush=True)
        return out

    # Read the density consumed by printing instead of applying spectral/print transforms.
    compile_shader("SpektraPrintScan", replace_once(
        print_shader, "vec4 pixel = loadFilmDensityPixel(index);",
        "vec4 pixel = loadFilmDensityPixel(index);\n  storeDestinationPixel(index, pixel); return;"))
    compile_shader("SpektraGrain", grain)
    patches = np.broadcast_to(np.array([.02, .05, .1, .18, .3, .5, .7, .9], dtype=np.float32)
                              .repeat(64)[None, :, None], (128, 512, 3)).copy()
    run("off", patches, {"grain-model": "off"})
    default = run("default", patches, {})
    for name, options in [
        ("samples12", {"grain-synthesis-samples": 12}),
        ("samples1024", {"grain-synthesis-samples": 1024}),
        ("unlayered", {"grain-synthesis-layered": "false"}),
        ("equal-rgb-radius", {"grain-synthesis-radius-scale-r": 1, "grain-synthesis-radius-scale-g": 1, "grain-synthesis-radius-scale-b": 1}),
        ("equal-layer-radius", {"grain-synthesis-layer-scale-0": 1, "grain-synthesis-layer-scale-1": 1, "grain-synthesis-layer-scale-2": 1}),
    ]:
        run(name, patches, options)
    cap8 = run("cap8", patches, {"grain-synthesis-cell-cap": 8})
    result["cellCap8And32ExactForThesePatches"] = bool(np.array_equal(default, cap8))

    compile_shader("SpektraGrain", replace_once(grain, DENSITY_RETURN, "return targetCenterDensity;"))
    run("target-layered", patches, {})
    for layer in range(3):
        compile_shader("SpektraGrain", replace_once(grain, DENSITY_RETURN,
                       f"return layer == {layer}u ? targetCenterDensity : 0.0;"))
        run(f"target-layer{layer}", patches, {})
        compile_shader("SpektraGrain", replace_once(grain, DENSITY_RETURN,
                       f"return layer == {layer}u ? covered / max(float(sampleCount), 1.0) : 0.0;"))
        run(f"coverage-layer{layer}", patches, {})

    compile_shader("SpektraGrain", grain)
    flat = np.full((256, 256, 3), .5, dtype=np.float32)
    common = {"grain-synthesis-layered": "false", "grain-synthesis-observation": 0}
    for name, options in [
        ("flat-off", {"grain-model": "off"}),
        ("flat-sigma0-epsilon1e4", {}),
        ("flat-sigma0-epsilon1e2", {"grain-synthesis-epsilon": .01}),
        ("flat-sigma0-epsilon1e6", {"grain-synthesis-epsilon": .000001}),
        ("flat-sigma20", {"grain-synthesis-observation": 20}),
        ("flat-sigma20-samples1024", {"grain-synthesis-observation": 20, "grain-synthesis-samples": 1024}),
    ]:
        run(name, flat, common | options)

    cases = result["cases"]
    checks = {}
    for layer in range(3):
        density = np.array(cases[f"target-layer{layer}"]["patchMeans"])
        coverage = np.array(cases[f"coverage-layer{layer}"]["patchMeans"])
        error = float(np.max(np.abs(coverage - (1 - 10. ** -density))))
        checks[f"layer{layer}CoverageMaxAbsError"] = error
    target = np.array(cases["flat-off"]["meanDensity"])
    sigma0 = cases["flat-sigma0-epsilon1e4"]
    prediction = (1 - 10. ** -target) * np.array(sigma0["maximum"])
    checks["sigma0PredictedMeanDensity"] = prediction.tolist()
    checks["sigma0MeanDensityPredictionMaxAbsError"] = float(np.max(np.abs(prediction - sigma0["meanDensity"])))
    checks["sigma0MeanTransmissionMaxAbsError"] = float(np.max(np.abs(10. ** -target - sigma0["meanTransmission"])))
    checks["passed"] = (all(checks[f"layer{i}CoverageMaxAbsError"] < .025 for i in range(3))
                        and checks["sigma0MeanDensityPredictionMaxAbsError"] < .02
                        and checks["sigma0MeanTransmissionMaxAbsError"] < .003)
    result["mechanismChecks"] = checks
    result["sourceHashesAfter"] = {str(path): sha256(path) for path in watched}
    result["originalFilesUnchanged"] = before == result["sourceHashesAfter"]
    # Restore the copied PrintScan resource so the test-only readback cannot be mistaken for a normal renderer.
    shutil.copy2(renderer.parent / "shaders/SpektraPrintScan.comp.spv", work / "runtime/shaders/SpektraPrintScan.comp.spv")
    shutil.copy2(renderer.parent / "shaders/SpektraGrain.comp.spv", work / "runtime/shaders/SpektraGrain.comp.spv")
    (work / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    if not checks["passed"] or not result["originalFilesUnchanged"]:
        raise AssertionError(f"Diagnostic mechanism or preservation check failed; see {work / 'results.json'}")
    print(f"PASS: model mechanism and unchanged original resources. Tonal limitation remains. {work / 'results.json'}")


if __name__ == "__main__":
    main()
