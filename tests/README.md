# Tests

Each folder is an independent test program with its own README and command. Run commands from the repository root.

## What you need

- **Panel tests (C#):** .NET SDK 10 and an installed Capture One 16.9, because the panel is built against its managed assemblies. No Capture One window is opened.
- **Renderer tests (C++/Python):** a built renderer and a Vulkan device. Build with `Build-Windows.ps1 -StageRuntime` first.
- **Python tests:** Python 3.13 with `requirements-build.txt`.

Three panel tests (`CapabilitiesTests`, `OptionWiringTests`, `OrchestrationReviewTests`) need a staged runtime: the folder with `stocks.json`, `renderer/` and `profiles/`. They use `SPEKTRAFILM_TEST_RUNTIME` if it is set, otherwise the repository root, which is a staged runtime after `Build-Windows.ps1 -StageRuntime`.

```powershell
dotnet run --project tests/CapabilitiesTests -c Release
```

`PaperDecorationIntegrationTests` takes the path of the built panel as its argument, for example `-- panel/NativePanel.dll`.

## Notes on the individual READMEs

- **Output folders.** Tests write to `artifacts/`, which Git ignores. Paths such as `artifacts/...` or `build/...` in a README are examples of such local folders, not files of this repository.
- **Fixtures.** Reference data (`fixtures.json` and similar) is checked in. Some READMEs name the scripts that produced it (`generate_fixtures.py`, `generate_oracle.py`, `test_reference.py`). The ones that need an installed copy of the reference implementation are not part of this repository; the tests run without them.
- **Tests inside Capture One.** Checks of the live Viewer, export and saved looks were made in Capture One on copies of catalogs with a separate test hook. That hook is tied to a private test session and is not included.
