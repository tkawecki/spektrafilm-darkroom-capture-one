# Export routing regression tests

Run with .NET 10 on Windows:

```powershell
dotnet run --project tests/ExportRoutingTests -c Release
```

The project compiles the actual `NativePanel/ExportHook.cs` against narrow managed host doubles. It starts neither Capture One nor an OS dialog, reads no photos or session databases, and does not change user settings. Production compilation against Capture One's actual assemblies remains a separate check.

The three entry paths (open dialog, native confirmation, and Export with Previous Settings) cover selected-variant and primary-only modes, mixed Enabled states, and native fallback. Additional checks cover cancelled/successful completion, pending export re-entry, empty selection, recipe restrictions, stale documents, and restoring native callbacks on disposal.

Before the fix, 14 groups failed: multi-selection was rejected and callbacks discarded `editMultiple`. The original callback shape in the test fixture exists only to make that regression reproducible; it is not a production compatibility path.

Real Capture One UI validation on copies is still required for host command bindings and actual per-variant outputs.
