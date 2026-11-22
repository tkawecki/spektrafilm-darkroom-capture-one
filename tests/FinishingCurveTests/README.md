# Arbitrary finishing curve editor and storage

```powershell
dotnet run --project tests/FinishingCurveTests -c Release -- artifacts/full-function-20261116/finishing-curve-tests.json
dotnet build NativePanel/NativePanel.csproj -c Release -o build/finishing-curves/panel
```

The first test failed before implementation: arbitrary X=0.17320508075688773 could not be loaded as an editable point. Final tests run without displaying windows or operating C1. They cover:

- Exact double roundtrip for every supported point count (2–64), including arbitrary X; no five-position snapping.
- Strict finite 0–1 validation, point shape/count, duplicate X, malformed/oversized data; input sorted by X without changing values.
- Cached immutable-array identity, equivalent formatting and concurrent parsing.
- Adding, moving, deleting, immutable originals, adjacent-X constraints, minimum two/maximum 64 points.
- 67 curves and 68,608 graph samples exactly matching the existing `FinishingTransform` (maximum difference zero), including endpoint extension and nonmonotonic curves.
- The real editor is constructed/layout-rendered offscreen without `Show`/`ShowDialog` or a PresentationSource. Numeric input tests preserve an untouched arbitrary coordinate exactly, accept precise percentages and reject crossing a neighboring point. An offscreen PNG is saved beside the report. Real mouse interaction in C1 is the parent integration check.

## Integration contract

```csharp
ImmutableArray<FinishingPoint>? edited = FinishingCurveEditor.Edit(owner, "Master curve", existingPoints);
if (edited is { } points)
    selections["finishing-curve-master-points"] = FinishingCurveStorage.Serialize(points);
```

`Edit(Window? owner, string title, ImmutableArray<FinishingPoint> points)` returns `null` for Cancel/close. It does not mutate input or saved settings. Default/empty input displays the identity curve. Apply returns the actual point array; Reset restores `(0,0),(1,1)` in the dialog and only Apply commits it. Mouse click adds, drag moves without crossing neighboring X, right-click/Delete removes while retaining two points. Selected input/output can be entered as percentages in the current culture. There is no JSON UI.

`Parse(string)` accepts a JSON array of numeric `[x,y]` pairs as an internal storage format. Empty/whitespace returns `default`, meaning **no arbitrary override**, so existing five-slider data can remain authoritative. Malformed values throw `FormatException`; callers should preserve the original saved string and report the error rather than silently erase it. Duplicate X is rejected to avoid silently losing a point. Sorting does not change coordinates. Parsed arrays are interned in a bounded thread-safe cache (256 text keys), so ordinary repeated settings reads preserve record/cache identity. Eviction can cause a later parse to produce a new equal-value array.

`Serialize(points)` is culture invariant, uses double roundtrip JSON numbers, and accepts `default`/empty as `""`. Invalid nonempty arrays throw `ArgumentException`. `FinishingCurveStorage.Identity` is the shared explicit straight curve. `Validate` returns sorted, validated points and preserves the input array if already valid/sorted (except identity, which is interned).

The graph evaluates `FinishingTransform(new FinishingSettings { Curve = points })`; it does not implement an alternate spline. It therefore shows the existing reference Catmull-Rom/1024-entry LUT behavior, including clamping, rather than a straight polyline between handles. Finishing's source/math/licensing are unchanged. Red/green/blue channel curves use the same scalar curve math; the caller selects the channel and supplies its points/title.

Storage, numeric/editor helpers and tests are standalone. They do not touch `SpatialPreview`, `Finishing.cs`, `SpektraFilmTool`, original photographs, user settings, export or OS configuration. The parent task owns wiring selection keys, five-slider fallback, preset import and C1 copy-session verification.
