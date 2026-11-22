# Production wide export integration tests

```powershell
python tests/ExportColorIntegrationTests/generate_fixtures.py
dotnet run --project tests/ExportColorIntegrationTests -c Release -p:NoWarn=MSB3277
```

The suite uses reflection to call the actual `SpatialPreview.DecodeExportColorManaged`, `DecodeExportWithFinishing`, `DecodeLinearDng` and `ResultEncoding` from the built panel. It never opens Capture One or a visible window, touches a session or reads photographs.

The linear DNG path additionally compares 48 unbounded float outputs against the independent Colour/SciPy oracle. Negative values and highlights above 1 survive finishing, micro contrast and partial strength. Cropped output equals a crop of the full processed frame; disabled linear-BT.2020 source bypass is float-bit exact, including alpha. The complete suite currently passes 115,369 assertions across six groups. Actual DNG and JPEG containers are checked separately in `tests/ExportFormatTests`.

Expected values are independently generated with Colour 0.4.7 derived matrices/Bradford/TRCs and SciPy's Gaussian filter. The 144 cases combine four source spaces, three wide output spaces, strengths 0/0.27/1, neutral or nonlinear pointwise finishing, and micro contrast off/on. They retain actual float32 transport precision, encoded-sRGB finishing order, linear strength mixing and final destination-only clamp. The micro oracle explicitly processes the full frame before a crop.

Checks include:

- Independent RGB16 output agreement within one code; RGB48/RGBA64, source alpha and true 16-bit values.
- Exact crop-of-fully-processed-frame equivalence, including micro contrast neighborhoods.
- Zero-strength source bypass, including identity of matching source/output profiles and cross-space source conversion.
- Nine primaries outside sRGB recovered in Adobe RGB, ProPhoto and linear Rec.2020. Exact Colour expectations account for float32 cancellation near zero; the looser ideal-primary check separately acknowledges fractional-gamma amplification of that transport error.
- Byte-identical sRGB-destination forwarding to the existing production export function.
- Native signed gamma2.4 output normalization versus linear renderer output, before strength and finishing.
- Missing/nonfinite rendered data and cancellation; rendered float buffers remain unchanged.

The initial integrated run exposed a five-code error in the new wide path's partial-strength/source baseline, which still used legacy rounded source matrices. The old sRGB pipeline should remain compatible; this correction belongs only in the new unbounded wide-export path. The suite also covers the native signed gamma2.4 negative-latitude case identified during review.

These tests validate the production color/finishing functions, not C1 source development, worker configuration or UI button wiring. Root owns actual per-variant C1 export and renderer smoke tests on copies. Profile embedding and independent ICC matrix/TRC checks are in `tests/ExportColorTests`.
