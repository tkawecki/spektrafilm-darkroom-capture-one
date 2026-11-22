# Native film aging

The generic reference 2.2.3 expiration macro and manual controls are baked into native
film density curves and grain sublayers before spectral Print/Scan. Storage
rates and macro coefficients are copied from `utils/expiration.py`; they are
empirically tuned artistic approximations, not measured aging laws for each
stock. Default disabled and enabled/all-neutral are strict no-ops.

The original native sampled profiles are retained. Aging gamma scales their
exposure coordinates per channel, then speed loss shifts them by
`speedEV*log10(2)`. Headroom-weighted fog and dye scaling use the reference formulas:
`D' = scale * (D + min(fog,0.9*Dmax)*(1-D/Dmax))`; sublayer fog is distributed
by layer amplitude and applied once. Total/layers are kept consistent when
initially consistent; independent legacy fitted residuals are retained otherwise.
Maxima are recomputed together with the baked arrays. The old film gamma and
push/pull controls retain their existing shader semantics and act once.

Native density curves are already normalized at generation time. Added aging
fog remains in this CMY buffer and therefore in actual spectral absorption;
it is not subtracted again or added separately to each of the three layers.
The adaptation is compatible with the existing native stock and is not a
claim of absolute pixel parity with newer reference fitted profiles. Grain-only aging
does not touch density curves. Grain area multiplies the resolved gain after
Production/RMS calibration (equivalent to RMS target times sqrt(gain)). Preview
uses the same area. Synthesis has its independent radius model, also independent
of particle-area aging in reference; its previously documented tonal limitations remain.

| CLI | Default | Range |
| --- | --- | --- |
| `--film-aging-enabled` | 0 | boolean |
| `--film-age-years` | 0 | 0..100 |
| `--film-age-storage` | room | frozen, cold, room, hot_humid |
| `--film-age-fog-r/g/b` | 0 | 0..1 |
| `--film-age-gamma`, `--film-age-gamma-r/g/b` | 1 | 0.05..2 |
| `--film-age-density` | 1 | 0.05..2 |
| `--film-age-grain` | 1 | 0.05..8 |
| `--film-age-speed-ev` | 0 | -3..6 |

RenderParams: `filmAgingEnabled`, `filmAgeYears`, `filmAgeStorage`
(`FilmAgeStorage` enum 0 frozen, 1 cold, 2 room, 3 hot/humid), `filmAgeFogR/G/B`,
`filmAgeGamma`, `filmAgeGammaR/G/B`, `filmAgeDensityScale`, `filmAgeGrainGain`,
`filmAgeSpeedLossEv`. Static resources cache these settings; changed/disabled
parameters restore the appropriate original or baked arrays without changing
saved profiles or per-document/variant data.

Build with the 26-profile resources and the updated RMS shader:

```powershell
cmake -S tests/AgingTests -B build/aging/tests -A x64
cmake --build build/aging/tests --config Release --parallel 4
python tests/AgingTests/test_reference.py --probe build/aging/tests/Release/FilmAgingProbe.exe
cmake -S ofx-still-renderer -B build/aging -A x64 -DOFX_BUILD=build/full-function-profiles/resources -DVulkan_INCLUDE_DIR=build/native40/deps/Vulkan-Headers-1.4.328/include -DVulkan_LIBRARY=build/native40/deps/vulkan-1.lib
cmake --build build/aging --config Release --parallel 4
python tests/AgingTests/test_renderer.py --exe build/aging/Release/SpektraStillRenderer.exe --baseline build/bases/Release/SpektraStillRenderer.exe
python tests/AgingTests/test_grain_fog.py --exe build/aging/Release/SpektraStillRenderer.exe
```

The CPU test runs the real reference expiration functions (unrelated chemistry import
omitted) against the same native input arrays. It covers 351 combinations of
26 stocks plus a consistent synthetic three-layer fixture. It verifies exact
macro values, fog absorption/Dmax, dye scale, speed loss, neutral/disabled and
grain-only identity, and all layer maxima; observed error was below 1.8e-7.
The grain diagnostic compares fog's effect with Grain Off, Production and RMS
in both Print and Scan, and checks the expected approximately doubled standard
deviation when area gain is four. These tests use synthetic frames only.
