# Stage-by-stage parity with the Python reference

No Capture One process or user image is needed. `stage_parity.py` renders a synthetic ProPhoto chart (neutral ramp,
hue sweeps, primaries/secondaries, smooth field) through the still renderer and through the Python reference in
`upstream-spektrafilm/src` (the `main` commit recorded in `public/UPSTREAM-CHANGES.md`). It compares every stage
boundary the two share:

| Renderer `--debug-tap` | Python tap | Quantity |
|---|---|---|
| `film-log` | `log_e_film` | log10 film exposure after camera diffusion and halation |
| `film-density-pre-grain` | (none) | CMY film density after development and DIR couplers, without grain |
| `film-density` | `cmy_film` | CMY film density with grain |
| `print-log` | `log_e_print` | log10 print exposure after print diffusion (print simulation only) |
| `print-density` | `cmy_print` | CMY print density (print simulation only) |
| (no tap) | `rgb_out` | output RGB, sRGB-encoded (compared in 8-bit code values) |

`--debug-tap` is a diagnostic option of `SpektraStillRenderer.exe` (2026-11-23). It works only for untiled frames in
print-simulation and scan modes, skips the developed-negative cache, and is not reachable from the panel. Without it
the renderer output is bit-identical to the renderer without the option (checked on 8 looks and with
`tests/GrainMeanTests/film_stage_cache.py`, 147/147). The print taps need the `SpektraPrintScan.comp.spv` built from
this repository (glslang `-V`; the unchanged source reproduces the installed shader byte for byte).

```powershell
artifacts\quality-audit-20261115\venv\Scripts\python.exe -I tests\ParityTests\stage_parity.py <renderer dir>\SpektraStillRenderer.exe upstream-spektrafilm\src --regions --json artifacts\parity\stage-parity.json
.venv\Scripts\python.exe -I tests\GrainRmsTests\closed_loop_rms.py <renderer dir>\SpektraStillRenderer.exe --json artifacts\parity\closed-loop-rms.json
```

The first needs a Python with the reference's runtime dependencies (numba, scipy, colour-science, scikit-image,
opt-einsum, pyfftw, OpenImageIO, rawpy, exiv2, lensfunpy, matplotlib); the audit venv has them. The second needs only
NumPy. Both drive the renderer through the `--export-stream` protocol (`tests/GrainMeanTests/compare_renderers.py`).

## Mapping and switches

- Both sides: spatial and stochastic effects off (Python `debug.deactivate_spatial_effects` and
  `deactivate_stochastic_effects`; renderer grain off, halation/scatter 0, diffusion filters off, DIR blur 0, scanner
  off), auto exposure off, output sRGB.
- Spectral upsampling: Python `hanatos2025` with its default fitted adaptation window
  (`settings.apply_hanatos2025_adaptation_window = True`) is the renderer's **`hanatos2026`**. The renderer's
  `hanatos2025` is the archived 2025 bandpass, which is the panel's default method (`SpektraFilmTool.cs`
  `DefaultSpectralMethod`); the case `portra400-endura-m2-panel-method` measures it.
- Colour adaptation: the renderer applies its input gamut compression (and output lightness/chroma compression) only
  with `--color-adaptation true`. The panel sends true ("Color adaptation", on by default); the renderer's own default
  is false. Python has input and output gamut compression on by default.
- Input: the renderer's `prophoto` is ROMM-encoded ProPhoto (what the panel sends), so Python runs with
  `io.input_cctf_decoding = True`.
- DIR (case `*-dir-*`): Python's digested coupler matrix and amounts are passed to the renderer option by option.
- Exposure: `--film-exposure` = `camera.exposure_compensation_ev`, `--print-exposure` EV = `log2(enlarger.print_exposure)`,
  and Python runs with `enlarger.print_exposure_compensation = False`: the renderer times the print for a 0 EV mid
  grey, so film exposure also changes print brightness (intended, see below).

## Results 2026-11-23 (renderer `43BE601B`, print shader `6D94D65E`)

Full output: `artifacts/parity/stage-parity-20261123b.{txt,json}` (local, not in Git). An earlier run without
`--color-adaptation true` and with the renderer's `hanatos2025` reported differences of up to 1.08 in film log
exposure; those came from that mapping, not from the renderer.

With the panel's colour adaptation and the matching spectral method the stages agree closely:

| Stage | Portra 400 / Endura, model 2 | Vision3 500T, model 2 |
|---|---|---|
| film log exposure | neutrals 0.000, saturated max 0.035 (model 1: 0.042) | max 0.012 |
| film density | max 0.025 | max 0.012 |
| print log exposure | max 0.038 | max 0.024 |
| print density | max 0.058 | max 0.051 |
| output (x255) | mean 1.3, neutrals max 3.7, saturated max 20 | mean 1.8, max 19 |

Scan mode (Portra 400): output mean 0.6/255, max 3.7/255. DIR couplers (non-spatial): film density max 0.037, output
mean 2.0/255.

Differences that remain (each a separate decision; fixes that change the look go into a new film model version):

1. **The panel's default spectral method is not Python's.** With the panel default (`hanatos2025`, archived
   bandpass) the film log exposure differs from Python main by up to 0.45 in saturated colours (neutrals 0.037),
   print density by up to 0.60 and the output by up to 43/255 (mean 1.9/255). `hanatos2026` matches Python.
   The default was kept in `docs/PLUGIN-AUDIT-20261117.md` as "Hanatos 2025, as in Python": Python's method is
   named `hanatos2025` but applies the fitted adaptation window by default, which is the renderer's `hanatos2026`.
2. **Print exposure compensation (intended, not a defect):** Python re-times the print for the film EV by default
   (`enlarger.print_exposure_compensation`), so film exposure barely changes print brightness. The renderer, like the
   upstream OFX plug-in, times the print for 0 EV (`printMidgrayExposureFactor`), so more film exposure gives a
   denser negative and a brighter print; the panel's Film exposure tooltip says so, and Print exposure brings the
   brightness back. With Python's compensation on, the neutral print log exposure differs by 0.167 per film EV
   (output mean 14-39/255 at +-1..2 EV); with it off, within 0.005 (output mean 0.9-2.1/255). Already found in
   `docs/PLUGIN-AUDIT-20261117.md`. The script now compares with compensation off.
3. **Profile data version (seen first on Velvia 100):** the renderer's tables are generated from
   `upstream-ofx/Resources/data/profiles` (profiles dated 2026-05-14), Python main ships newer profiles (2026-05-28,
   with a `density_curves_model` and a CC BY-SA 4.0 licence). Density curves differ in all but six stocks: within the
   range used in practice by up to 0.025 (Portra 400, Vision3 500T), at curve ends more (Velvia 100 Dmax G 3.6235 vs
   3.5719, which is the measured 0.05; cinema print stocks 2383/2393 up to 0.6). Updating changes every stock's look.
   **Done as film model 3 (2026-11-23):** a second table set from Python main's profiles with neutral print filters
   fitted by `upstream-ofx/tools/fit_neutral_print_filters.py`; `stage_parity.py --sweep 3` compares every film
   (output mean 0.7-1.4/255, greys within 5.5/255 at the ramp ends, mid grey neutral by construction).
4. **Saturated output in print mode:** up to 20/255 while print densities agree within 0.06. **Diagnosed 2026-11-23:**
   the output gamut compression algorithm. The renderer compresses in OkLch (`SpektraPrintScan.comp`,
   `SpektraScannerPost.comp`), Python main defaults to CAM16-UCS (`OutputGamutCompressSpec.algorithm`). With Python
   switched to OkLch (same knees) the saturated output differs by at most 2.7-3.2/255 in print and 1.7/255 in scan
   (Portra 400 / Portra Endura, model 3); with compression off in both, 1.6-3.3/255. Porting CAM16-UCS would change
   the look of saturated colours, so it needs a new film model.
   **Done as film model 4 (2026-11-24):** CAM16-UCS with the reference's Cp_max table. `--sweep 4` against `--sweep 3`
   (renderer `build/model4`, all 26 films): largest output difference 1.8-6.7/255 (median 2.8) instead of 14-22/255
   (median 18.4); slides mean 0.02/255. Full output `artifacts/model4/sweep-m{3,4}.{txt,json}` (local).

## Film model 4 checks (2026-11-24)

```powershell
artifacts\quality-audit-20261115\venv\Scripts\python.exe -I tests\ParityTests\autoexposure_parity.py <renderer dir>\SpektraStillRenderer.exe upstream-spektrafilm\src
.venv\Scripts\python.exe -I tests\ParityTests\dir_guard_sweep.py <renderer dir>\SpektraStillRenderer.exe --model 4
.venv\Scripts\python.exe -I tests\ParityTests\model_identity.py <installed renderer>\SpektraStillRenderer.exe <renderer dir>\SpektraStillRenderer.exe [--tile 512]
```

- `autoexposure_parity.py`: the renderer's metered EV (read from the film-log tap, auto exposure on minus off) against
  `measure_autoexposure_ev` on the reference's small preview, seven methods at 3000 x 2000, 1997 x 3001 and 200 x 150.
  Model 4: all within 0.0001 EV. Model 3 (`--model 3`, earlier sampling): up to 0.11 EV for median, 0.03 EV for
  center weighted. The scene keeps highlights below ROMM 1.5, the top of the renderer's decoding table.
- `dir_guard_sweep.py`: film density along a grey exposure ramp at three strong DIR settings must follow exposure
  (rise for negatives, fall for slides). Model 3: 60 of 78 film/setting pairs fold; model 4: none.
- Scanner cases (`*-scanner-m3/m4`, Python's unsharp mask 0.7 px / 0.7 and a 0.7 px scanner blur): the raw output
  comparison reports up to 43/255 because Python's sharpened output goes below black; clipped to the image range,
  model 4 is within 2.4-3.3/255 of Python, model 3 26.7/255 (compression after the sharpening).
- `model_identity.py`: older models against the installed renderer across 13 configurations (auto exposure, DIR,
  output compression, grain, scanner, scan modes). 2026-11-24: models 1-3, 39 of 39 identical; with 512 px tiles,
  models 1 and 3, 26 of 26. After the scanner order change (14 configurations, including scanner with colour
  adaptation): models 1-3 42 of 42, tiles 28 of 28; model 4 differs from its first build only with the scanner on.

## Grain RMS closed loop (`tests/GrainRmsTests/closed_loop_rms.py`)

Measures the RMS granularity in the rendered negative (film-density tap minus the grain-free tap, averaged over 8x8 px
cells equal in area to the 48 um aperture at 5.317 um film pixels) at net density 1.0, against the manual RMS target,
for Portra 400 (two targets), Vision3 500T, Ektar 100 and C200, three seeds each:

| Regime | Measured / target (R, G, B) |
|---|---|
| single layer, grain blurs off (checked, tolerance 5%) | 1.017-1.039 |
| emulsion sublayers on (renderer default), blurs off | 0.80-1.01 (Vision3 500T R/G 0.84/0.80) |
| sublayers and the default grain blurs (as in a look) | 0.65-0.82 |

The single-layer result validates the measurement and the area solve. Causes of the deficit (diagnosed 2026-11-23):

- **Sublayers:** at net density 1.0 the fastest sublayer is fully developed (Portra 400 G: p = 1.00, the middle one
  0.93). `SpektraGrain.comp::particleDevelopedDensity` clamps the developed particle count of its Gaussian branch at
  the *expected* seed count, which removes most of the upward deviation of a saturated layer (simulation: RMS 0.58 of
  the model at p = 1 for any particle count; isolated layer 0 measured 0.70, mixed with the unsaturated layers). The
  area solve and Python's `layer_particle_model` (Binomial(Poisson(N/s), p), no cap) keep it. Model 2 already dropped
  the cap in its exact branch (fewer than 8 expected particles), not in the Gaussian branch.
- **Final grain blur:** sigma 11.8 um (35 mm equivalent, the upstream OFX plug-in's fixed-size version of Python's
  0.65 px) lowers the RMS seen through the 48 um aperture to 0.83 of the target; dye-cloud blur and microstructure do
  not change it measurably. The area solve does not include it.

Both mean that a look with "RMS 10" shows less grain than the stock's RMS 10. The RMS definition itself is unchanged
(project decision, 2026-11-23).

**Fixed in film model 2 (2026-11-23):** no cap in the Gaussian branch, and the area solve divides by
`apertureVarianceAfterBlur` (final blur sigma in film um; checked against a blurred white-noise simulation: sigma
11.8 um leaves RMS 0.69, the renderer's default 7.17 um 0.81, as measured). Model 2: 1.00-1.04 of the target in all
three regimes for the five cases; model 1 unchanged.
