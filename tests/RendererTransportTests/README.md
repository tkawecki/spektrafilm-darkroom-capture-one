# Real managed renderer transport tests

Windows x64 / .NET 10. This console project links the production `OpticalRenderer.cs` and telemetry, with a logging-only StartupHook stub. It does not launch Capture One or modify saved looks. It needs a complete copied runtime (renderer executable, resources and shaders), a native fixture captured from a verified copy, and baseline BGRA bytes.

From the repository root:

```powershell
$dotnet = 'dotnet'
& $dotnet run --project tests/RendererTransportTests -c Release -- artifacts/native40-performance/runtime artifacts/native40-performance/native-fixture.json artifacts/native40-performance/baseline.bgra artifacts/native40-performance/managed-shared.json lifecycle
```

Checks: known full-size BGRA hash, float output equality with the pipe transport, map growth/shrink, 1 × 1 alpha, cancellation during a full render and exact recovery. The final `lifecycle` argument enables those additional checks. Timing records cover one cold and four warm requests and are separate from the lifecycle checks.

Since 2026-11-18 the panel sends `HELLO 2` before the first frame. A renderer without the handshake is refused, so the old-runtime fallback run below now ends with a mismatch message; it is kept as a record. Current checks:

```powershell
& $dotnet run --project tests/RendererTransportTests -c Release -- handshake <runtime-with-an-older-renderer>
& $dotnet run --project tests/RendererTransportTests -c Release -- serial <runtime-with-the-current-renderer>
```

- `handshake`: an older renderer is refused with "does not match this panel" on both attempts.
- `serial`: 12 overlapping requests on one worker equal the same requests rendered alone. Queued requests are cancelled by `Dispose`, and new ones are refused.
- Without the request semaphore, `serial` fails with interleaved replies.

Historical: run against the preserved old runtime to test MAP rejection and automatic pipe fallback (omit `lifecycle` for the recorded compatibility test):

```powershell
& $dotnet run --project tests/RendererTransportTests -c Release -- backups/Before-Native40-20261115/Experimental artifacts/native40-performance/native-fixture.json artifacts/native40-performance/baseline.bgra artifacts/native40-performance/managed-legacy.json
```

The deliberately malformed worker reproduces a MAP acknowledgement error. Build it separately and copy only that executable to an isolated fake runtime:

```powershell
cmake -S tests/RendererTransportTests -B build/native40/fault-worker -G 'Visual Studio 17 2022' -A x64
cmake --build build/native40/fault-worker --config Release
New-Item -ItemType Directory -Force build/native40/fault-runtime/renderer
Copy-Item build/native40/fault-worker/Release/SpektraStillRenderer.exe build/native40/fault-runtime/renderer/
& $dotnet run --project tests/RendererTransportTests -c Release -- fault build/native40/fault-runtime
```

The fake worker answers `HELLO 2` correctly, so the test reaches the malformed acknowledgement. Both consecutive attempts must reject it. Before the fix, the first failure left a mapping attached and the second call incorrectly accepted uninitialized pixels; adding `InvalidDataException` to production cleanup made the regression pass. Never place this fake worker in a real runtime.
