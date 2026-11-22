# Grain mean-colour restore tests

Drive a copy of `SpektraStillRenderer.exe` over its `--export-stream` protocol with synthetic images (no photos, no Capture One, no saved looks). Run with any Python that has NumPy, e.g. the staged reference interpreter with `-I`.

| Script | What it checks |
|---|---|
| `measure_grain_mean.py --exe EXE --out OUT.json [--grain production] [--set grain-mean-restore=true]` | Per-patch mean ratio grain/grain-free at several resolutions (8 flat colour patches, linear Rec.709 output). |
| `compare_renderers.py --baseline OLD --candidate NEW --out OUT.json` | 11 configurations: NEW without the option must be bit-identical to OLD; NEW with the option must be finite. |
| `tile_check.py EXE` | Forced 512×256 tiles vs full frame, with and without the option (expects exact equality). |
| `texture_check.py --exe EXE --size 6000x4000` | Grain strength (relative std) and its 16 px coarse component with and without the option. |
| `timing.py EXE 7728x5151` | Warm render time: grain off, grain, grain + restore. |
| `boost_check.py EXE FIXTURE.json [name=value…]` | Highlight boost change vs boost 0 for several protections on a captured fixture. |
| `boost_visual.py EXE FIXTURE.json OUTDIR` | Crops: no boost / boost with mid-grey protection / boost from the brightest highlight. |
| `visual_check.py EXE FIXTURE.json OUTDIR` | 1:1 crops from a captured copy-only C1 fixture (off / on / grain-free) and an ×8 difference. |
| `aperture_scan.py --exe EXE --out OUT.json --rule fixed:1 [--set grain-synthesis-aperture=true]` | Grain synthesis at 6000/3000/1500/750 px: mean ratio without and with the mean restore, grain strength, and the strength of the 6000 px render box-downscaled to each size. Rules `fixed:UM`, `pixel:K` (window K × film pixel), `pixelmin:K` (max(1 µm, K × pixel)). |
| `aperture_identity.py EXE` | Grain synthesis aperture on/off: bit-identical at 6000×4000 and 6720×4480 (film pixel ≤ 6 µm), different at 3000 and 1200 px. |
| `noise_match_identity.py EXE` | Grain synthesis noise matching on/off: bit-identical at 6000×4000 and 6720×4480 and without aperture integration, different at 3000 and 1200 px with it. |
| `noise_match_edge.py EXE` | Grain synthesis noise matching at a sharp edge (750 px): the averaged profile with matching stays as close to 640-sample point sampling as 128-sample point sampling does. |
| `transmission_variance.py [--realizations N]` | CPU only: the size-matching variance formulas for the transmission-domain final blur (reference and native render) against a Monte Carlo disc Boolean model on a 0.2 µm raster. |
| `cpu_bias.py` | CPU-only Monte Carlo of the Synthesis pixel estimator: density bias and transmission ratio for one layer. |

Results of 2026-11-17 are summarised in docs/GRAIN-MEAN-RESTORE-20261117.md; Grain synthesis aperture integration (2026-11-18) in docs/SYNTHESIS-CALIBRATION-20261118.md; noise matching (2026-11-19) in docs/SYNTHESIS-NOISE-20261119.md.
