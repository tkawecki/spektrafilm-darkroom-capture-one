# Export progress lifecycle

```powershell
dotnet run --project tests/ExportProgressTests -c Release
```

The suite links the production helper and passes 17 assertions. It creates WPF objects but never calls `Show`, starts Capture One, captures the desktop or changes focus. It verifies that a disabled source panel does not disable Cancel, one-way live status binding, repeated Cancel, manual close, owner close, normal `Dispose` without cancellation, handler/binding cleanup, and a cancellation callback racing completed/disposed work. Other callback errors remain visible instead of escaping an event handler.

Production API: `ExportProgressWindow.Show(Window? owner, TextBlock statusSource, Action cancel)` returns `IDisposable`. Use it around the awaited export and dispose the returned scope before disposing the cancellation source. With a visible owner, the modeless window is owned/centered on that owner; this includes the native modal export dialog, whose main-window controls are unavailable. Without a visible owner it does not request activation. Closing the progress window or owner requests cancellation once; programmatic `Dispose` closes silently. Actual modal interaction remains a C1 integration check on a copied session.
