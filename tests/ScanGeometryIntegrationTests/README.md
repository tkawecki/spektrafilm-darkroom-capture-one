# Post-scan geometry verification

Run from the repository root with .NET 10:

```powershell
$dotnet = 'dotnet'
& $dotnet run --project tests/ScanGeometryTests -c Release
& $dotnet run --project tests/ScanGeometryBitmapTests -c Release
& $dotnet run --project tests/ScanGeometryIntegrationTests -c Release '-p:NoWarn=MSB3277'
```

The integration test reflects the real `SpatialPreview` implementation. It does not start Capture One, show windows, read photographs or change user parameters. The default run includes a synthetic 8000×6000 float RGBA frame (about 2 GB peak process memory); `-- --skip-large` omits that single case. Synthetic output containers and verification JSON are written beneath `artifacts/full-function-20261116/scan-geometry-integration/<run-id>`.

Fixtures are reproducible with `python tests/ScanGeometryIntegrationTests/generate_fixtures.py`. They use Colour 0.4.7 and SciPy for independent conversion/finishing/micro contrast, then the authenticated installed reference implementation 2.2.3 CPU `apply_scan_geometry` and `crop_image` sources. Source hashes and dependency versions are recorded in `fixtures.json`. The generator stubs only package initializers, avoiding unrelated GUI/GPU/RAW initialization; the two actual CPU modules are loaded unchanged.

Coverage: four source and target RGB spaces; 0/.27/1 strength; identity and gamma/contrast/saturation finishing; micro contrast; full-frame processing before C1 crop; horizontal magnification and vertical reduction with anti-aliasing; skew and scan crop; HDR and negative RGB; separate alpha coverage; dimensions; output ICC; RGB16 packing; TIFF/PNG/JPEG/DNG byte-exact neutral delegation; missing/nonfinite pixels and cancellation. Dedicated math fixtures cover tiny images, edge crops, both skew signs, desqueeze 0/±1 and range extremes, and pixel-centre mapping.

2026-11-16 results: math **177,231** assertions, maximum CPU error **1.171710e-7**; bitmap **540** assertions; production integration **70,258** assertions, no failures. The 192 independent finishing results match production float32 exactly; 384 geometry results have maximum error **2.545477e-7**. The 48 MP run (partial strength, .5 desqueeze, 2° skew, scan crop) took **2.159 s**, peak working set **2,025,799,680 bytes**. This is one local synthetic measurement, not a C1 end-to-end benchmark.

Latest proof: `artifacts/full-function-20261116/scan-geometry-integration/d64cf3a771ed454b91478b79919bc909/verification.json`; tested `NativePanel.dll` SHA256 `2BA912C71A41C4768B72CA0D773388E86FA13538F32F90EB0D7F8E63859EEF7B`. Root may subsequently rebuild the shared panel as other features change.

After adding all 17 export pairs, the fixture suite now includes **348** finishing pipelines and **696** CPU geometry results. The additional run passes **125,633** assertions, with float32 finishing still exactly matching the independent oracle and the same maximum geometry error. It omits the already measured 48 MP test (`--skip-large`); proof is in `artifacts/full-function-20261116/scan-geometry-integration/17a27fe7695c47a3b630a1fd3823f52f/verification.json`.
