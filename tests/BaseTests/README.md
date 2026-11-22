# Spectral film and paper bases

`SpektraBaseDensity.h` adjusts the static absorption spectrum before Print/Scan.
It does not apply an RGB tint after rendering. Film base affects Print and Scan;
paper base is ignored in Scan. Disabled or neutral controls retain the original
upload path, including the original unmeasured spectral support (NaNs) of older
negative profiles. No measured samples become negative and no missing spectra
are fabricated.

CLI: `--film-base-enabled`, `--print-base-enabled` are booleans, default 1.
Both prefixes accept `-scale`, `-cyan`, `-magenta`, `-yellow` in 0..5, default 1.
`--film-base-tilt` is -2..2, default 0. RenderParams uses `filmBaseEnabled`,
`filmBaseScale`, `filmBaseTilt`, `filmBaseCyan/Magenta/Yellow` and corresponding
`printBase*` fields without tilt.

Reference: official installed reference 2.2.3 `spektrafilm/model/develop.py`, functions
`base_film_density_tuning`, `base_print_density_tuning`, `_tuned_base_density`.
Gaussian kernels use the same 4-sigma truncation and reflected boundaries.
Film uses status-M peaks 460/555/650 nm, 35 nm smoothing, and the nonnegative
spectral tilt around 555 nm. Paper uses status-A peaks 445/530/610 nm and 20 nm
smoothing. Controls act on spectral optical density, so doubling uniform
density squares transmitted light rather than merely changing RGB saturation.

Two compatibility corrections are intentional:

* reference 2.2.3 multiplies film scale twice when tint or tilt is nonneutral, once when
  neutral. Native applies scale once consistently; tiny tint edits do not jump.
* reference replaces a paper's complete base spectrum at the first nonneutral CMY edit.
  Native transfers its relative smoothed spectral shape onto the legacy base:
  `legacy * scale * smoothed(custom peaks) / smoothed(neutral peaks)`.
  This preserves the original spectrum at neutral, stays nonnegative, and
  avoids a discontinuity or the negative densities possible with an additive
  residual. A zero reference shape keeps scale-only behavior at that wavelength.

Tests use source functions extracted from the actual reference file, with only their
ordinary NumPy/SciPy dependencies, plus the real Vulkan frame bridge:

```powershell
cmake -S tests/BaseTests -B build/bases/tests -A x64 -DOFX_BUILD=build/full-function-profiles/resources
cmake --build build/bases/tests --config Release --parallel 4
python tests/BaseTests/test_reference.py --probe build/bases/tests/Release/BaseDensityProbe.exe
python tests/BaseTests/test_renderer.py --exe build/bases/Release/SpektraStillRenderer.exe --baseline build/exhaustion/Release/SpektraStillRenderer.exe
python ofx-still-renderer/smoke.py --exe build/bases/Release/SpektraStillRenderer.exe
```

The original 364-case legacy matrix had maximum error 1.81e-7; the current
442-case matrix also includes the six appended stocks. Default and disabled Vulkan
parity, all nine axes, Print/Scan boundaries, REUSE, alpha/finite output and the
existing smoke/export suite passed. `build/bases` is an isolated, nondeployed
build. No C1 session or user photo is opened by these tests.
