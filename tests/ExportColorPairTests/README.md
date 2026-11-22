# All reference implementation image-export color pairs

Run from the repository root:

```powershell
dotnet run --project tests/ExportColorPairTests -c Release
python tests/ExportColorPairTests/verify_lcms.py
```

`generate_fixtures.py` uses independent Colour 0.4.7 conversion functions and matrices. Its 3,570 cases exercise four conversion directions, negative values, transfer thresholds and highlights across all 17 unique gamut/transfer pairs. A separate captured fixture contains 3,360 conversions from the verified four-space baseline assembly (hash embedded in that fixture); all 10,080 double result bit patterns must remain identical. Source input values and output bit patterns are synthetic, not photographs.

ICC tests independently parse the actual embedded matrix and TRC, compare D50 PCS against Colour, check 65,536 points per curve for monotonicity, and verify black/white endpoints and SHA256 identity. `verify_lcms.py` runs 3,773 synthetic patches through LittleCMS 2.17 via Pillow 12.1.1, using relative colorimetric intent and NOOPTIMIZE to avoid the unrelated 8-bit precomputed CLUT approximation. Its RGB8→Lab8 result is a consumer-compatibility check; the double PCS oracle and separate RGB16 roundtrip suite verify precision.

2026-11-16: **970,949 checks PASS**, maximum relative conversion error **3.114766e-8**, maximum tested ICC PCS error **1.398886e-5**. LittleCMS: all 11 new profiles accepted, maximum **1 Lab8 code** difference. `artifacts/full-function-20261116/export-color-pairs-lcms.json` records versions and each result. `ExportColorTests` passes 34,052 assertions, `ExportFormatTests` 262, and `ScanGeometryBitmapTests` 628 after extending ICC roundtrips to all 15 ICC-backed pairs. Log metadata-only output is verified by the metadata integration work separately.

The exact nominal BT.2020 and V-Log formulas have tiny discontinuities at their rounded cutoffs. Tests explicitly accept either side only when matrix summation lands within 1e-14 of the corresponding exact threshold; ordinary samples retain the strict tolerance. BT.2020's pixel inverse uses OETF(.018), not .081. Its ICC curve uses a monotonic table because a valid ICC TRC must not reverse brightness; see the profile README.
