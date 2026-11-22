# Additional colour profiles

No Capture One process or user photo is needed. The compatibility test uses only Python and NumPy:

```powershell
python -B -m unittest discover -s tests\ProfileTests -p test_profile_compatibility.py -v
```

After generating the 26-profile resources and building a separate renderer, compare it against the unchanged previous build:

```powershell
python -B tests\ProfileTests\profile_expansion.py --baseline build\full-export\Release\SpektraStillRenderer.exe --candidate build\full-function-profiles\renderer\Release\SpektraStillRenderer.exe --output artifacts\full-function-20261116\profile-render-verification.json
```

The Vulkan test renders a synthetic colour pattern through all 20 historical profiles in Print and Scan using Hanatos 2025, Hanatos 2026 and Mallett 2019 (120 cases), requiring bit-exact float output. It then requires finite, unique output from all six additions in both modes and all three methods (36 cases). The extra reversal film remains Scan-only in the panel; direct Print evaluation checks renderer stability only. The six new native Hanatos 2025 bandpasses are explicitly derived from fitted reference windows, not archived measurements; the adapter is described in `generate_profile_curves.py` and the sources in THIRD-PARTY.md. These checks do not assert photographic accuracy, calibrated neutral balance, or equivalence to the entire reference implementation pipeline.
