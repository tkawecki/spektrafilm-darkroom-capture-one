# RMS calibration and renderer tests

No Capture One process or user image is needed. The source oracle is the exact reference 2.2.3 `resolve_grain_model` function, executed alone from its archived Python AST. NumPy is the only test dependency.

```powershell
cmake -S tests/GrainRmsTests -B build/full-function-profiles/rms-tests -G "Visual Studio 17 2022" -A x64
cmake --build build/full-function-profiles/rms-tests --config Release
python -B tests/GrainRmsTests/test_reference.py --probe build/full-function-profiles/rms-tests/Release/GrainRmsProbe.exe --output artifacts/full-function-20261116/rms-reference.json
python -B tests/GrainRmsTests/test_renderer.py --baseline build/full-export/Release/SpektraStillRenderer.exe --candidate build/full-function-profiles/renderer/Release/SpektraStillRenderer.exe --reference artifacts/full-function-20261116/rms-reference.json --output artifacts/full-function-20261116/rms-renderer.json
```

The candidate must be rebuilt with the 26-profile tables and updated `SpektraGrain.comp.spv`. Rebuild that shader from source with glslang `-V`; the C++ frame int buffer now includes RMS flag index29. Do not combine the new renderer with an older grain shader.

The reference test covers all 26 colour films, both layer modes, stock/manual targets and four strengths (208 cases). The renderer test preserves all 20 historical films in Print/Scan and Off/Preview/Production/Synthesis (160 exact comparisons), checks RMS on all 26 films/modes/layer configurations (104 finite renders), and exercises manual/stock target choice, seed changes, strength, exact Production equivalence, amount zero, repeated deterministic rendering, aging gain4 versus strength2, and bit-exact Print/Scan tiled versus full-frame output. Low-strength tests detect an accidental reintroduction of the manual Production area floor.

RMS controls: `--grain-model rms`, `--grain-rms-stock true|false` (default true), `--grain-rms-r/g/b 0.1..100` (manual defaults7/7/12), `--grain-rms-strength 0.001..4` (default1). Density minima, uniformity and layer scale use the existing controls. RMS does not change Production defaults, saved parameters, export resolution or sampling quality.

## Closed loop (2026-11-23)

`closed_loop_rms.py <SpektraStillRenderer.exe>` measures the RMS granularity in the rendered negative through the
renderer's `--debug-tap` stage taps (48 um aperture, net density 1.0) against manual targets; NumPy only. The single
layer case is checked (5%), sublayers and look defaults are reported. Results and method: `tests/ParityTests/README.md`.
