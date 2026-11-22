# Quality regression tests

Windows, installed Capture One 16.9.0.3894 and .NET 10 are required. No C1 process is launched and no original user data is written.

```powershell
dotnet run --project tests/QualityTests -c Release
# Optional copied-look persistence test: input backup and separate output directory.
dotnet run --project tests/QualityTests -c Release -- '<folder with saved looks>' artifacts/quality-look-roundtrip
```

Checks real production helpers/controls: global and local process-settings value copies, Linear Response lookup and failure, WPF downsampling and exact 100% pixels at four DPI values, new/legacy sparse defaults, settings roundtrip, and inactive diffusion controls retaining values while Off. Optional persistence uses the production VariantLookStore and isolated per-document output paths.

Real C1 source preparation, visual display and export checks are separate and are done in Capture One on copies of a catalog.

`Capabilities.cs` additionally checks Scan/Print/Scanner enablement, retained inactive values and their reload, plus nonzero unsupported developer exhaustion in both preview and export options. The parameter is preserved and explicitly marked as not applied in the optical UI. Failures before the correction and passing results afterward are archived under `artifacts/audit-fixes-20261116`.
