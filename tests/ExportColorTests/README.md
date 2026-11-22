# Unclipped export color and ICC tests

Run with .NET 10 from the repository root:

```powershell
dotnet run --project tests/ExportColorTests -c Release
python tests/ExportColorTests/generate_fixtures.py
```

The second command regenerates independent numerical fixtures using the installed Colour 0.4.7 library from the read-only reference distribution, with this Python environment's NumPy/SciPy. No C1 process, real photo, user session, monitor assignment or OS setting is changed. Outputs are synthetic fixtures and files under `artifacts/full-function-20261116/export-color-tests`.

## Integration contract

- `ExportRgbSpace`: `Srgb`, `AdobeRgb`, `ProPhotoRgb`, `LinearRec2020`.
- `ExportColorSpace.Parse(id)`: `srgb`, `adobe_rgb`, `prophoto`, `linear_rec2020`; missing/empty defaults to sRGB, unknown IDs throw.
- `FromSrgb(r,g,b,space)` and `FromLinearSrgb(r,g,b,space)` return `Rgb(Red,Green,Blue)` in the destination's encoding. `ToSrgb`/`ToLinearSrgb` reverse these conversions. Values remain doubles, unclipped and unquantized. sRGB-to-sRGB is bit-identical, including negative zero.
- `EncodeSrgb`/`DecodeSrgb` use the existing C# finishing convention: the linear toe extends through all negative values. This differs from the native shaders' signed-power extension. For wide export, request `linear_rec709` from either engine, then use `FromLinearSrgb` directly if finishing/strength permit; otherwise encode to this unbounded sRGB convention, finish/blend, then `FromSrgb`.
- `ResolveProfile(space, root?, captureOneDirectory?)` resolves only known working profiles: Windows standard sRGB; C1 `Color Profiles/Common/Adobe RGB (1998).icm`; runtime `profiles/preview/ProPhoto RGB.icc` and `Linear Rec.2020.icc`. The latter two are hash-checked against the already-used preview profiles. It does not use monitor/Eizo/camera profiles or change assignments.
- `SpektraExport.SaveWithProfile(path,image,token,profilePath)` embeds a structurally validated RGB ICC profile in RGB48 TIFF or RGBA64 PNG. **It does not transform pixels**. Call it only after conversion into that profile's space. Existing `Save(path,image,token)` retains its name/signature and standard sRGB behavior; it remains the sole method named Save for existing reflection callers.
- Clamp to `[0,1]` only after the color transform when packing the final 16-bit integer destination. Values still outside the destination gamut/range cannot be retained by TIFF RGB48/PNG RGBA64. No earlier sRGB clipping, 8-bit intermediate, perceptual gamut compression, or black-point compensation is added.

## Definitions and sources

Matrices are derived in double precision from published primaries and D65, with Bradford adaptation for ProPhoto to the existing ICC PCS D50 `(0.9642,1,0.8249)`. This matches the project's profile generator, rather than silently replacing it with a slightly different rounded D50 xy pair. The embedded profiles have the normal ICC fixed-point rounding.

- [ICC sRGB registry](https://registry.color.org/rgb-registry/srgb): primaries, D65 and piecewise transfer.
- [Adobe RGB (1998) specification](https://www.adobe.com/digitalimag/pdfs/AdobeRGB1998.pdf): Adobe primaries and exact gamma `563/256`. Negative values use a signed-power extension; final integer output is bounded.
- [ICC ROMM RGB specification](https://www.color.org/chardata/rgb/ROMMRGB.pdf): ProPhoto primaries, D50, gamma 1.8 and linear toe. The helper preserves unbounded intermediate values; it postpones the specification's output clipping to destination packing.
- [ICC BT.2020 registry](https://registry.color.org/rgb-registry/bt2020): BT.2020 primaries/D65. Our **Linear Rec.2020** option deliberately uses identity transfer, not the television gamma/OETF.
- [Colour RGB conversion source](https://github.com/colour-science/colour/blob/develop/colour/models/rgb/rgb_colourspace.py): independent matrix/adaptation fixture generation. The fixture records version 0.4.7; all spaces use derived transformation matrices.

## Verification and Windows CMM limitation

2560 numerical cases cover 4 spaces and finite negative, positive, toe, highlight and randomized values. The largest observed difference from Colour is `6.776e-8` encoded units, below 0.005 of one 16-bit code. The tolerance accounts for fractional-power amplification of tiny matrix roundoff at zero and Colour's continuous sRGB threshold versus the rounded IEC threshold. Twelve wide-primary cases distinguish conversion-before-clipping from conversion-after-clipping. Neutrality and unbounded inverse behavior pass.

TIFF/PNG tests verify all four destinations: exact 16-bit pixel/alpha roundtrip, exact embedded ICC bytes, non-8-bit sample values, cancellation, invalid RGB profile rejection before replacement, and byte-identical old/default sRGB files. Separately, 648 independent Colour PCS fixtures validate actual profile matrix/TRC tags; observed maximum XYZ error is `1.526e-5`, approximately one ICC signed-16.16 step.

**Windows CMM is not a numerical oracle for the pure matrix/TRC transform here.** On this installation WIC RGBA64 and WinICM `TranslateColors` with explicit relative intent produce identical but substantial differences at sRGB gamut boundaries: maximum 6960/65535 Adobe, 8557/65535 ProPhoto and 7582/65535 linear Rec.2020 over a 17³ grid. A corresponding Adobe test via Pillow/LittleCMS returns 2/255 where Windows returns about 0.106. The precise Windows-CMM cause is not established; do not label this as fixed-point rounding or claim exact WIC parity. The export path uses the independently verified double transform and embeds the validated standard profiles; it does not compensate its pixels for this Windows discrepancy. These CMM results are saved as diagnostics. [Microsoft documents rendering-intent differences](https://learn.microsoft.com/windows/win32/wcs/rendering-intents) and [supported WIC transform formats](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwiccolortransform-initialize); WPF RGB48 is not in that transform list, so the diagnostic uses RGBA64 without reducing precision.

The separate production reflection suite must verify pipeline placement, partial strength, finishing/micro, cropping and source bypass. The standalone helper suite alone does not establish that the panel requests unclipped renderer output or selects the intended ICC.
