# Exposure comparison

`ExposureComparison.Show(owner, title, baseEv, minEv, maxEv, render, apply,
isCurrent)` returns a modeless `ComparisonWindow`. The controls offer 3/5
frames, 1/3, 1/2 or 1 EV, full-frame comparisons and an exact composite strip.
The main panel supplies an immutable photo/look snapshot and renders each
absolute EV through its normal engine callback. Keep every candidate's source,
physical frame size, random seed and unrelated settings identical; do not crop
input into separate bands. The helper never writes settings while rendering.
`apply(ev)` is called only by the explicit Apply button and returns whether
the originating photo/look is still valid. The caller must recheck its identity
there. `isCurrent()` and `window.Invalidate(reason)` reject stale comparisons.

Renders run sequentially, accept cancellation and cannot publish obsolete
results. Closing cancels the job. Monitor conversion uses the same
`MonitorColorManagement.GetDrawing` / `PreviewDrawing` path as the Viewer;
original frames are never overwritten. The resolution is disclosed in the
window and has no effect on export quality.

The underlying deltas, fraction labels and band boundaries come from the
pinned reference 2.2.3 `test_strip.py`. Its GUI can also choose 4/6/7/8/9 bands;
this compact C1 helper intentionally offers the requested 3 and 5.
`generate_oracle.py` reads the archived, attributed upstream source through
the isolated reference runtime. Tests compare 6 plans, 24 partitions and two
byte-exact composite hashes, then cover explicit Apply, range rejection,
cancellation, stale identities and late-generation results. They open no UI
or Capture One.

```powershell
& build/reference-runtime/reference/python/python.exe -I -B tests/ExposureComparisonTests/generate_oracle.py
dotnet run --project tests/ExposureComparisonTests/ExposureComparisonTests.csproj -c Release --artifacts-path build/exposure-tests -- .
```

Modeless-window appearance, monitor movement and the parent panel's actual
worker/Apply integration still require the operator's C1 test on a copied
session. A standalone test pass is not evidence that this integration ran.
