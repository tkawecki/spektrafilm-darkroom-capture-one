# Scan finishing — CPU reference port

`NativePanel/Finishing.cs` implements the **pointwise** processing in the reference implementation
2.2.3 `spektrafilm/model/scan_finishing.py`. This is a port of inspected code,
not an inference from a feature list. It does not claim equivalence of the
complete C1 and reference implementation renderers.

The public Git tree does not contain this module; the official release ZIP
contains its plain Python source. The inspected installed copy and release
copy have the same SHA256:

```
scan_finishing.py: 95E3E778F9E55E68F8AC25DA1D4D67C0BBC6832CF72F5EE69081DED797F5D51E
reference-2.2.3-win-x64.zip: A965443B9FB5414F80BC3A682A9276244F576DCA4DF8CD7170C55F2C7055B94A
```

The inspected source is stored only in ignored
`artifacts/full-function-finishing/upstream/reference/_internal/spektrafilm/model/scan_finishing.py`.
Copyright remains with Andrea Volpato and the original authors (see THIRD-PARTY.md), GPL-3.0.
The C# modification notice and complete MIT notice for NegPy's separation matrix
(Copyright 2023 NRG Systems, Inc.) are in `Finishing.cs`.

## API and working space

```csharp
var settings = FinishingSettings.FromDictionary(savedNumericParameters);
var transform = new FinishingTransform(settings);
if (!transform.IsIdentity)
    transform.Apply(ref encodedSrgbRed, ref encodedSrgbGreen, ref encodedSrgbBlue);
```

Settings are an immutable record; the optional arbitrary curves use
`ImmutableArray<FinishingPoint>`. The transform copies/precomputes its private
tables and is safe to share between worker threads. Each `Apply` allocates no
objects. `IsIdentity` bypass and neutral `Apply` preserve every input bit,
including negative zero and finite values outside 0–1. Missing old-save keys
are neutral. Invalid numeric settings fall back to their neutral values;
finite settings outside supported GUI ranges are clamped.

Order, matching the reference:

1. Decode sRGB only when the linear group is active; multiply each channel by
   `2^(exposure + channelGain)`; optional row-normalized density-domain unmixing;
   encode sRGB again.
2. Master + channel levels, gamma on positive values, contrast around encoded
   0.5, exponential toe, exponential shoulder.
3. Rec.709-weighted saturation, vibrance weighted by the pixel's current HSV
   saturation, six smooth 60-degree hue saturation windows.
4. Add shadow, midtone and highlight zero-sum chroma tints using masks
   `(1-L)^2`, `4*L*(1-L)` and `L^2` from the same luminance value.
5. Compose master then per-channel Catmull-Rom curves into the same **1024-entry
   float32 LUT** as upstream, with linear interpolation. The source's old
   “PCHIP” comment is superseded by its actual Catmull-Rom implementation.

Without an active point curve, there is no display gamut clipping: values
outside 0–1 reach the caller for its final quantization. As in upstream,
separation uses a `1e-6` density floor and leaves original values at/below that
threshold unchanged; levels use a `1e-3` minimum denominator. An active curve
clamps its LUT and sample domain to 0–1 and flat-extends its endpoints. Split
toning preserves the channel sum, **not exact Rec.709 luminance**. No claim of
skin-selective vibrance is made; the inspected algorithm protects saturation.

The implementation guards final nonfinite arithmetic: NaN becomes 0 and
infinities saturate to finite `double` endpoints. This only differs from
upstream for pathological numeric overflows/nonfinite pixels, beyond normal
image buffer values. Settings supplied directly to the constructor are also
bounded. Custom curve points are sorted, duplicate x keeps the last point,
nonfinite points are discarded and coordinates are bounded to 0–1.

The pointwise API excludes upstream spatial `micro_contrast` (a 1.5-pixel luma
Gaussian unsharp operation), scan geometry, and other film/print processing.
The upstream functions operate in the selected output RGB space; this C1
adapter intentionally fixes **encoded sRGB** because that is its integration
boundary. Apply after conversion to sRGB and before final quantization.

## Persisted numeric keys

Every key below has prefix `finishing`. Ranges match the inspected reference implementation
GUI `widget_specs.py`; curve controls are the C1 adapter's five fixed x knots.

| Key suffix | Range | Neutral/default |
| --- | --- | --- |
| Active | 0 / 1 | 1 |
| ExposureEv | -4 … 4 | 0 |
| GainRedEv, GainGreenEv, GainBlueEv | -2 … 2 | 0 |
| ColorSeparation | 0 … 1 | 0 |
| BlackPoint | -0.25 … 0.5 | 0 |
| WhitePoint | 0.5 … 1.5 | 1 |
| BlackRed, BlackGreen, BlackBlue | -0.25 … 0.25 | 0 |
| WhiteRed, WhiteGreen, WhiteBlue | -0.25 … 0.25 | 0 (offsets) |
| Gamma | 0.2 … 3 | 1 |
| Contrast | -1 … 1 | 0 |
| Toe, Shoulder | 0 … 1 | 0 |
| Saturation, Vibrance | 0 … 2 | 1 |
| HueRed, HueYellow, HueGreen, HueCyan, HueBlue, HueMagenta | 0 … 2 | 1 |
| ShadowHue, MidtoneHue, HighlightHue | 0 … 360 degrees | 210, 120, 45 |
| ShadowStrength, MidtoneStrength, HighlightStrength | 0 … 1 | 0 |
| CurveBlack, CurveShadows, CurveMidtones, CurveHighlights, CurveWhite | 0 … 1 | 0, 0.25, 0.5, 0.75, 1 |
| CurveRedBlack … CurveRedWhite | 0 … 1 | same five defaults |
| CurveGreenBlack … CurveGreenWhite | 0 … 1 | same five defaults |
| CurveBlueBlack … CurveBlueWhite | 0 … 1 | same five defaults |

Hue values are inert when the corresponding strength is zero. The curve's
five x coordinates are 0, 0.25, 0.5, 0.75, 1; dictionary values are y. Full
arbitrary master and RGB curves are also available through `FinishingSettings`
properties `Curve`, `CurveRed`, `CurveGreen`, `CurveBlue`.

## Tests

No Capture One, network, images, third-party NuGet package or Python runtime is
needed to run the checked-in fixtures:

```powershell
dotnet run --project tests/FinishingTests -c Release
```

`fixtures.json` contains 2,280 RGB cases from the **actual** hash-pinned Python
module: individual operations, combined order, extremes, negative/over-white
latitude, chromatic and neutral inputs and deterministic random colors. The
test also checks exact neutral bits, duplicate/unsorted curve points, curve
endpoint clamping and undershoot, contrast pivot, neutral separation, split
toning masks, concurrent calls and numeric overflow. Tolerance is
`2e-12 * max(1, abs(expected))`, including the reference float32 curve table.

To deliberately regenerate fixtures, Python with NumPy/SciPy is required:

```powershell
python tests/FinishingTests/generate_fixtures.py artifacts/full-function-finishing/upstream/reference/_internal/spektrafilm/model/scan_finishing.py
```

The generator refuses a source hash mismatch. It imports this one file via
`importlib`, not the application. Optional `--benchmark` after `dotnet run`
prints scalar 2 MP timings; these are microbenchmarks, not C1 preview timings.
