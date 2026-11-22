# Production orchestration review checks

```powershell
dotnet run --project tests/OrchestrationReviewTests -c Release -p:NoWarn=MSB3277
```

25 reflection assertions against the actual panel: independent control contexts, saved/hidden engine choices, arbitrary curve X, retained missing recipe/filter identifiers, no persistence event during load, repeated load identity, invalid curve rejection before mutation, historical missing-parameter fallbacks, and captured export-gamut argument override without changing live controls. No C1/window is shown. The argument test exercises production `RendererOptions` and `OverrideOption`; it does not simulate a native SaveFileDialog. The review separately checked that both single and batch export call this override for the actual captured output profile.

Related checks run during this review: ExportRoutingTests 74 assertions; ExposureComparisonTests 106; LookVersionsTests 11; ExportProgressTests 17; MonitorColorTests offscreen profile/identity/cache/PNG preservation passed. Actual monitor assignment remained Windows sRGB with Advanced Color disabled. No OS settings or running C1 process were changed.
