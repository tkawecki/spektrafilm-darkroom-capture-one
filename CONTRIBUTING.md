# Contributing

Bug reports, suggestions and pull requests are welcome.

## Reporting a problem

Open an issue with the **Bug report** form. It asks for what is needed to reproduce the problem:

- the release version of the panel,
- your Capture One version (only 16.9.0.3894 is tested),
- your GPU and driver version,
- the relevant lines of `%LOCALAPPDATA%\FilmToolsForCaptureOne\panel.log`.

Please do not attach photos of people without their consent, and check the log lines for anything you do not want to share (folder names can contain names). Security problems go through the private route in [SECURITY.md](SECURITY.md), not an issue.

Fixes that also apply to the original [Spektrafilm for Capture One](https://github.com/nothing-complex/spektrafilm-for-capture-one) are best offered there as well.

## Building and testing

See [Build from source](README.md#build-from-source) for the tools you need. In short:

```powershell
.\Build-Windows.ps1 -BuildPanel -StageRuntime
dotnet run --project tests/CapabilitiesTests -c Release
```

`tests/` holds independent test programs (C#, C++ and Python); [tests/README.md](tests/README.md) says what each needs and how to run it. Run the tests that cover the part you changed.

## Pull requests

- Keep a pull request to one topic and describe what it changes and how you checked it.
- Do not commit Capture One files, photos, catalogs or sessions, build output (`panel/`, `renderer/`, `dist/`) or test output (`artifacts/`).
- The film and paper models come from [spektrafilm](https://github.com/andreavolpato/spektrafilm). Changes to the model itself are best made there first; this project follows its reference implementation.
- Keep the licence and author notices of the upstream code ([THIRD-PARTY.md](THIRD-PARTY.md)).

By contributing you agree that your contribution is licensed under the project's licence, GPL-3.0 ([LICENSE](LICENSE)).
