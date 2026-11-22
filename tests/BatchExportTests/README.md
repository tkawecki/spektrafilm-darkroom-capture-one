Run from the repository with .NET 10:

```powershell
dotnet run --project tests/BatchExportTests -c Release
```

The production helper is exercised by reflection, without launching C1.
81 assertions cover independent snapshots, document stores, protected source
paths, duplicate names/variants, case-insensitive collisions, a concurrent file
writer, serial callbacks, cancellation, partial failures, exact RGB48/RGBA64
TIFF/PNG values, sRGB ICC, ambiguous recipes, missing host targets, and masking
unsupported hidden inversion at render time while preserving saved controls.
All test outputs use a new GUID directory under ignored `artifacts/batch-tests`.

The C1-only complement is a batch audit run inside Capture One on a copied catalog. Its
test hook accepts `{"action":"batch-audit","full":true}` for native
size TIFF, or `full:false` for PNG at the single enabled and selected recipe's
size. It selects two variants in the copied collection, keeps the original
primary, isolates test look persistence, and restores the original selection
and controls in `finally`. It checks independent finishing snapshots, 16-bit
pixels/ICC, preservation of existing files and adjustment counters, and
cancellation after one completion. The single/batch comparison records hashes
of the actual C1 source buffers: identical sources require identical pixels;
if C1 redevelops different source pixels, the report records that difference
and the maximum/mean 16-bit output delta instead of attributing it to the batch.
No C1 process is started by this standalone test project. The C1 hook must be
explicitly run on approved copies; compiling it is not evidence it passed in C1.

Production `Capture` and `Entry.CaptureSource` must run on C1's UI thread before
background work. They keep explicit document/variant references and detect
adjustment changes. The scheduler accepts those references directly, so export
does not require changing the user's selection. Rendering remains delegated to
the shared full-depth export/finishing path; the helper serializes publication
and never overwrites a source or existing export.
