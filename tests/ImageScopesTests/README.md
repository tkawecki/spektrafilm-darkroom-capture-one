# Spektrafilm image scopes

Run with .NET 10 on Windows:

```powershell
dotnet run --project tests/ImageScopesTests -c Release
```

The test links the production math and WPF helpers. No Capture One process, original session, photos or visible OS window is involved. It creates synthetic bitmaps only and saves an offscreen rendering of the three plots under `artifacts/full-function-20261116/image-scopes-offscreen.png`.

## Integration contract

```csharp
ImageScopes.ScopeWindow scopes = ImageScopes.Show(owner, viewerFrame, "Spektrafilm image scopes");
scopes.Update(viewerFrame); // UI dispatcher; call when the final displayed frame changes
scopes.Closed += (_, _) => { /* clear the caller's reference */ };
```

Use the original sRGB `viewerFrame`, before monitor ICC conversion. The window is modeless and owns neither a timer nor any image capture. `Update` ignores the identical frozen bitmap, cancels older computation and publishes only the most recent result. `Close` cancels pending work. Existing exports and frame pixel data are never touched. Frozen inputs are reused; mutable inputs are cloned/frozen for a stable snapshot. Callers should close the window when the document binding is disposed.

## Definitions

- Input scope codes are straight-alpha BGRA8. RGB24, premultiplied BGRA and other WPF formats are converted by WPF format conversion, without ICC conversion. Higher-depth input is explicitly quantized to the displayed 8-bit scope range.
- RGB histograms have 256 bins. Encoded luma is `Y′ = .2126 R′ + .7152 G′ + .0722 B′`; the luma histogram rounds to the nearest code, halfway up. These are sRGB codes, not linear-light luminance or video IRE.
- Waveform stores count by original horizontal position and code, with image left/right preserved when sampling. It has 256 horizontal by 256 code bins. RGB and luma views share the same cached analysis.
- Full-range vectorscope coordinates are `Cb=(B′−Y′)/(2×.9278)` and `Cr=(R′−Y′)/(2×.7874)` for normalized RGB. Horizontal Cb increases rightward; vertical Cr increases upward. The grid maps `[-.5,.5]` to `[0,255]`, rounding halfway up. Markers denote 100% sRGB primary/secondary colors. They are not broadcast legal-range targets or an inferred skin-tone line.
- All pixels are analyzed up to 1,048,576. Larger frames use a reproducible centered regular grid bounded by that budget, maintaining the source aspect ratio as closely as the integer budget permits. The exact frame and grid sizes are stated in the window. Sparse clipping can be missed; sampled counts are estimates, not exhaustive image clipping measurements.
- Fully transparent samples are excluded. Partial alpha uses straight RGB without alpha weighting. Counts report RGB black/white, any channel at code 0/255 and individual channel boundary percentages among visible samples. This measures the supplied output frame, not prior clipping in RAW or high-dynamic-range values.
- Histogram can switch between linear/logarithmic count scale. Waveform and vector trace brightness are logarithmic for visibility. The statistics always use actual sample counts.

## Tests and evidence

Initial regression stage: all seven mathematical groups failed against the empty contract. Coverage now includes known color counts and luma bins, a complete neutral ramp, independent Rec.709 primary/chroma targets, transparency, deterministic sampling of small/large/thin frames, an independent random oracle for every scope bin, input immutability, invalid input and cancellation. WPF tests add exact bitmap extraction, RGB24/PBGRA conversion, mutable snapshot isolation, sampled source coordinates, stale-result rejection, cached identity, close cancellation and offscreen plots. A synthetic 8514×5676 frame records background analysis timing without enforcing a machine-specific performance threshold.

Scope tests do not establish Capture One integration: root must connect actual `viewerFrame` updates and verify on a test session copy. A test-strip presenter is not part of this helper; root still owns strip rendering and presentation.
