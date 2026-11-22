# Production finishing integration tests

These tests load the built `NativePanel` assembly and invoke its actual private
`DecodeResultWithFinishing` and `DecodeExportWithFinishing` paths via reflection.
They create synthetic float RGBA frames in memory, require no photographs or
Capture One process, and never modify a session/catalog or installed runtime.
The build uses local C1 assembly references and .NET 10/WPF.

```powershell
dotnet run --project tests/FinishingIntegrationTests -c Release
```

Coverage:

- Exact neutral/disabled comparison against the legacy wrappers for all four
  input spaces, all three output encodings, strength 0/0.27/1, full/cropped
  frames and RGB48/RGBA64 output.
- Actual upstream Python fixtures from `FinishingTests/fixtures.json` through
  production 16-bit export and BGRA8 preview, including every finishing group
  and the combined operation order. Maximum allowance is one code for float
  transport/quantization; the standalone double helper has tighter tests.
- Independently calculated linear-light Look-strength blending, including
  Adobe RGB, ProPhoto and linear Rec.2020 baseline conversions.
- Non-square crop origin/size, source alpha (renderer alpha is not trusted),
  all output encodings and genuine 16-bit values rather than expanded 8-bit.
- Highlight recovery from over-white renderer pixels before clipping, plus
  negative shadow recovery by levels.
- Show source/zero strength bypass, NaN/Inf renderer rejection and cancellation.
- A 4,002,000-pixel preview crossing the production parallel-row threshold;
  every channel is compared with a one-pixel result. Captured settings remain
  unchanged after mutating the originating parameter dictionary.

Known build warning MSB3277 is inherited from the C1 assembly references.

Static integration review (2026-11-16): final/draft finishing settings are
captured before render awaits, results are gated by `inputRevision`, and raw
`renderedPixels`/`draftPixels` are reused when only finishing changes. All three
packed-BGRA eligibility gates require finishing `IsIdentity`. Source comparison
and strength zero bypass finishing. These observations are code review,
**not runtime evidence for C1 selection changes, UI timing or stale-result
rejection**; those require the separate C1-on-copies test hook.

The review found a minor avoidable recomputation: record equality on
`ImmutableArray` curve fields uses backing-array identity. Capturing an unchanged
active point curve creates a new array, so a separately queued redraw misses the
`renderedFinishing` equality cache. The raw film result is still reusable and
pixels remain correct. This does not cause an unprompted render loop because the
outer `queued` gate remains in force.
