# Production paper decoration integration

Run from the repository root with .NET 10. This test loads an existing panel DLL and **never rebuilds the shared NativePanel project**, starts Capture One, opens a window, reads photographs or writes user settings.

```powershell
dotnet run --project tests/PaperDecorationIntegrationTests -c Release
```

The optional first application argument is an absolute panel DLL path; the default is `build/full-function/panel/NativePanel.dll`. The runtime texture store, images and proof JSON are isolated beneath `artifacts/full-function-20261116/paper-integration/<run-id>`.

`python tests/PaperDecorationIntegrationTests/generate_fixtures.py` combines the existing independent Colour/SciPy/actual reference CPU geometry fixtures with unchanged ASTs of reference 2.2.3's `apply_paper_finish_to_export` and padding functions, and the actual `paper_texture.py` module. It records source hashes. The oracle processes target-encoded data after finishing/micro contrast, C1 delivered crop and scan geometry. Padding is consistently enabled, the approved correction to the reference implementation's differing single/batch behavior. Alpha coverage is the separately documented C1 extension.

Coverage includes 348 finishing pipelines and 1,392 paper results in all 17 target spaces; source strengths 0/.27/1; negative/HDR values; micro before crop; two geometry configurations; ordered multi-layer blending and white borders; exact production preview and RGB16 writer pixels against the separately tested helpers; TIFF/PNG profiles and log outputs without false ICC; JPEG quality routing; linear DNG RGB16 payload; byte-exact neutral delegation to every old writer; source/render buffer immutability; missing active assets; and cancellation before destination mutation.

Verified 2026-11-16: **376,851 assertions, no failures**, 373,056 independent oracle samples, maximum absolute float error **2.980232e-7**, 169 synthetic output files. Panel SHA256 **D1219DEBD92A04D3A1A04E4EA94207EA94A6F19BA21A19F1AE4FB25B74EA8BE3**. Proof: `artifacts/full-function-20261116/paper-integration/3ca0eeea4905461b9bfa0ed998c9567f/verification.json`.

The test also emits `artifacts/full-function-20261116/paper-texture-fixture.png`, a synthetic 3×2 RGB fixture for the separately authorized C1 audit. Copy it to `SPEKTRAFILM_BENCH_ROOT/paper-texture-fixture.png` before dispatching `PaperAudit`. The C1 scenario remains unexecuted; local test results do not imply deployment or real-host validation.
