# Windows monitor ICC: audit and paint-only correction

The test uses Windows/.NET 10 WPF, reads installed ICC files and display metadata, and renders only offscreen. It never opens a window, changes the monitor/OS, operates Capture One, or reads original photos. No installed ICC file is modified. `inspect_monitor.py` uses Python's standard library and documented read-only Win32/WCS calls.

```powershell
python tests/MonitorColorTests/inspect_monitor.py artifacts/full-function-20261116/monitor-icc-inventory.json
dotnet run --project tests/MonitorColorTests -c Release -- artifacts/full-function-20261116/monitor-icc-wpf.json
dotnet build NativePanel/NativePanel.csproj -c Release -o build/monitor-icc/panel
dotnet run --project tests/QualityTests -c Release
```

## Update 2026-11-18: explicit sRGB source

WPF's `ColorContext(PixelFormats.Bgra32)` follows the system's standard-sRGB setting. On this machine it later returned an Eizo `CX271(31973036)01Custom.icc` ("02Paper") profile, while the display had `CX271(31973036)04sRGB.icc` assigned.

The panel and this test now use Windows' `sRGB Color Space Profile.icm` as the sRGB source (`NativePanel/SrgbProfile.cs`). Results with that source match the 2026-11-16 measurements:
- 04sRGB: maximum 4 codes;
- system sRGB: 0 codes.

For an sRGB monitor the expected paint pixels are the unchanged source; WIC's sRGB-to-sRGB transform is not used as the reference. `systemSrgbMatchesWpf` in the report records whether the WPF context still equals the sRGB file.

## Observed Windows state, 2026-11-16

- One active monitor: CX271, `\\.\DISPLAY1`, RTX 4090; 8-bit SDR.
- `QueryDisplayConfig` / `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2`: supported, **not active**, mode SDR=0. Therefore Windows ACM is not supplying a display transform in this configuration.
- `GetICMProfileW`: system `sRGB Color Space Profile.icm`, SHA256 `2B3AA1645779A9E634744FAF9B01E9102B0C9B88FD6DECED7934DF86B949AF7E`.
- `ColorProfileGetDisplayUserScope`: system scope. `ColorProfileGetDisplayDefault` system/user and WCS default queries: profile-not-found. The active monitor's registry `ICMProfile` value is empty; HKCU ICM associations absent. Thus GDI supplies its sRGB fallback, not an Eizo calibration.
- Custom/Adobe RGB/sRGB/CAL3 Eizo ICC files exist in the Windows color directory. Their presence does **not** establish assignment or the current physical monitor mode. The software does not choose one by filename or change that assignment.

## Host and WPF evidence

REA read-only metadata/IL inspection of CaptureOne.dll SHA256 `5C90325F3AF2B79A884B01F4A074AB7BB9E2A98F061913E30C866FE4AD3CEDF9`, MVID `ff6daefd-abf5-4021-b9b8-4a6bc47369aa`, shows `WPFControlWithGDIOnPaint.OnRender` (`0x06003c86`) calling `DoGDIDrawing` (`0x06003c88`). The latter pushes a DPI transform, raises Paint with the DrawingContext, then pops it. It has no ICC conversion call. These tokens identify this build only. The retained full REA bundle is ignored in artifacts; the focused evidence is `monitor-icc-host-evidence.json`.

The old `PreviewDrawing.Create` takes a raw BGRA BitmapSource and draws it. The test links this actual production helper: opaque sample pixels remain byte-exact through WPF offscreen rendering. Explicit WIC sRGB→Eizo conversion produces different pixels, also preserved exactly by drawing. This proves the difference in the tested paint bitmap path; **an offscreen test by itself does not prove desktop compositor behavior**. The conclusion combines the native display state, host call path and Microsoft's documented SDR behavior.

Primary sources:

- [Microsoft: ICC profile behavior with Advanced Color](https://learn.microsoft.com/en-us/windows/win32/wcs/advanced-color-icc-profiles): Windows does not color-manage application output when Advanced Color is inactive. When active it treats normal integer surfaces as sRGB, with a separate process-wide legacy ICC compatibility helper.
- [GetICMProfileW](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-geticmprofilew): reads the output profile of a display DC, independent of whether ICM is enabled on that DC.
- [ColorProfileGetDisplayDefault](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-colorprofilegetdisplaydefault) and [WcsGetUsePerUserProfiles](https://learn.microsoft.com/en-us/windows/win32/api/icm/nf-icm-wcsgetuseperuserprofiles) describe the read-only assignment queries.
- [.NET WPF BitmapSource.Create](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/Imaging/BitmapSource.cs) constructs a CachedBitmap from pixels. [ColorConvertedBitmap](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/Imaging/ColorConvertedBitmap.cs) explicitly calls the WIC color transform with source and destination contexts.

## Implemented contract

`MonitorColorManagement.cs` is used only by `ViewerPreview.Paint`. `Attachment.Bitmap` / original Drawing, SpatialPreview's `viewerFrame`, saved-preview PNG and export remain the original sRGB data. The transform is applied after all Spektrafilm finishing, solely for the paint surface.

The monitor is selected by the greatest intersection with the Viewer's screen rectangle (`MonitorFromRect`), not by the panel position. Assigned SDR profiles are queried with a monitor-specific DC. Profile queries and file reads run on a worker; initial read and display moves temporarily use sRGB and expose a status message. A background dispatcher timer checks the active Viewer's monitor/profile every five seconds. It only invalidates the Viewer when profile metadata/state changes.

The cache key includes source bitmap identity, monitor/device, profile path, modification time, length, and color mode. The two most recent painted frames are retained. A nonidentity WIC transform is materialized once into a frozen BitmapSource and DrawingGroup, so pan/zoom/repaint does not recompute it. An assigned profile byte-identical to WPF's built-in sRGB profile returns the original bitmap/drawing without a conversion or pixel copy. An Eizo profile named sRGB is still converted because its calibration differs.

On query/conversion failure the original sRGB surface is retained and a distinct `ViewerPreview.ColorManagementWarning` is reported/logged. It is separate from `UnavailableReason`: UI status may display either, but saving the original preview must only depend on actual availability. Profile files fixed/reassigned later are retried by the periodic query.

Advanced Color/HDR policy is an explicit **sRGB bypass**, with a status/log message. No physical monitor ICC is additionally applied. The process-wide Windows legacy ICC compatibility helper has not been validated here and is named in the message; correct color in that configuration is not claimed. The current machine is SDR/ACM-off. A Viewer spanning different monitors chooses one profile; per-monitor clipping is not implemented. Only one physical monitor was available for this audit; monitor-switch cache keys were tested synthetically, not by moving C1 between two displays.

## Verification

The initial regression failed before implementation: `Assigned monitor profile must transform the Viewer paint bitmap to match WIC`. After implementation the same requirement passes for all four read-only Eizo fixtures and system sRGB over 65,536 samples (32³ RGB grid repeated): exact WIC output, original pixels/PNG byte-exact, cache reuse, source/profile/monitor keys, timestamp invalidation, missing profile fallback and Advanced Color bypass. Real production P/Invoke profile discovery matches the independent inventory: DISPLAY1, sRGB, SDR, no warning. QualityTests passes with zero failures; panel builds with zero errors (existing MSB3277 assembly-version warning).

The fixture differences are potential corrections, **not measured errors on the current display**: Custom/AdobeRGB maxima 145/153 codes of 255 over saturated synthetic samples. Their actual applicability depends on the monitor mode and calibration assignment. Direct WIC conversion of 1600×1000 takes about 8 ms here; a repeated cache lookup measured below 0.0001 ms. Native profile query measured about 4.6 ms warm and remains off the UI thread. Current sRGB has no conversion/copy cost.

Evidence files: `monitor-icc-inventory.json`, `monitor-icc-wpf.json`, `monitor-icc-quality.log`, `monitor-icc-host-evidence.json`, `monitor-icc-verification.json` under `artifacts/full-function-20261116`. These are diagnostics, not deployment. A C1 test on a copied session remains the parent task's integration check.
