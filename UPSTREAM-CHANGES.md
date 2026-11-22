# Changes from the upstream projects

Spektrafilm Darkroom for Capture One is a **modified version** of three GPL-3.0 projects. This file states what was changed, as the licence requires. Authors and licences are listed in [THIRD-PARTY.md](THIRD-PARTY.md).

| Part | Upstream | Starting point |
|---|---|---|
| Capture One panel, launcher, installer | [Spektrafilm for Capture One](https://github.com/nothing-complex/spektrafilm-for-capture-one) by Luka Kauzlaric | branch `experimental/unchecked-capture-one`, commit `0607311` (2026-11-14) |
| Vulkan renderer (`upstream-ofx`) | spektrafilm OFX by Aedan Diez | commit `86476afc5b077de77e2278e3658d1ba9309892a1` |
| Python reference and film profiles (`upstream-spektrafilm`) | [spektrafilm](https://github.com/andreavolpato/spektrafilm) by Andrea Volpato | commit `3bb2c2d2801ff68b92019cf1dbcbb133d60832bc` |

The changes below were made between 2026-11-15 and 2026-11-21 by tkawecki. Two sets of fixes were later ported from the original panel's commit `285f0aa` ("Harden saved looks, rendering, exports and installer upgrades"); the files concerned say so in their headers.

## Panel (`NativePanel`, launcher, installer)

**Host**
- Works with Capture One 16.9.0.3894 (export dialog, selection, Viewer overlay). The launcher warns when another version is installed.
- Several catalogs and sessions in one Capture One process; settings stay separate per document and per variant.
- Own product name, installation folder and installer marker, so it does not replace or modify an installation of the original project.

**Looks and data safety**
- Undo and redo, named look versions, automatic versions before actions that replace a whole look, copy and apply between variants, presets.
- Saved looks keep fields this version does not know; unreadable looks are protected instead of overwritten; looks are backed up at start.
- Looks made with a second processing engine, which was removed again, are recognised and refused instead of being reinterpreted.

**Input from Capture One**
- "Neutral for film" input: the image is developed for the simulation with the camera's Linear Response curve and without sharpening, Clarity and Structure. Input colour spaces sRGB, Adobe RGB, ProPhoto RGB and Linear Rec.2020.
- Capture One's Enhanced Denoise is followed: the image is developed again when Capture One has finished its denoise file.
- New looks start with ProPhoto input, automatic film exposure and colour adaptation.

**Viewer**
- Native-resolution Viewer frames and "Auto", which renders at the on-screen size and at full size when zoomed in.
- Quick frames from Capture One's own preview after selecting an image or adjusting it in Capture One; drag previews that are not dropped while a slider moves; an adaptive wait before a full develop.
- Monitor colour management off the UI thread with an exact colour table per monitor profile.
- Scopes, pinned reference comparison, exposure strips.

**Export**
- Capture One's own Export writes the Spektrafilm image through Capture One's recipes. The render is opened by Capture One as a temporary image outside the catalog and written by its image core, so the recipe's location, token file names, format (including PSD and PSB), ICC profile, size, resolution, metadata, watermark, output sharpening and crop-as-path apply as in an ordinary Capture One export. Several recipes at once, one render per group of recipes with the same size, crop and colour space; a list of all files before the export; Capture One's collision rules, never replacing an original or a file next to the originals; an export journal so an interrupted export can be resumed; the look described in the file's XMP. Counters in file names follow Capture One's export: one number per image, shared by all its recipes. In a catalog, which has no output location, a recipe that writes there is refused with a note instead of being written to the program's folder. JPEG sources that were not shown yet no longer fail their first batch or recipe export. The image settings are read again for every file, as Capture One's own export does, which removes a crash in Capture One's image library seen while it rebuilt thumbnails. Capture One 16.9 writes 16-bit PNG samples in the wrong byte order; the panel repairs such files.
- Up to 100 megapixels and 16,384 px per side, tiled above 32 MP; batch export of selected variants with each variant's own settings; a progress window with cancel.
- TIFF and PNG (16-bit), JPEG, linear DNG; export colour spaces with generated ICC profiles.
- Colour-only LUT export from the native renderer (.cube, Hald CLUT, OCIO bundle).

**Controls**
- Post-scan finishing (exposure, contrast, micro contrast, levels, curves, hue/saturation, split toning), scan geometry, paper textures and border.
- Film and paper base density, emulsion aging, print developer exhaustion, RMS grain, Grain synthesis options, highlight boost for halation, scanner white and black levels.
- Controls that cannot affect the current mode are disabled and keep their values; a warning when DIR coupler settings change tone strongly.
- Automatic film exposure and metering under Defaults, look versions directly below, full slider names.

## Renderer (`upstream-ofx`, `ofx-still-renderer`)

- Input reuse on the GPU across render requests (from the original panel, 2026-11-14).
- A still-image wrapper with a streaming protocol, a version handshake, serialised requests and a shared-memory output for native Viewer frames; tile sizes for native previews.
- Viewer frames packed by the renderer, including look strength, identical to the managed conversion.
- Grain: mean-colour restore, an RMS grain model, and for Grain synthesis aperture integration, noise matching, a final blur in transmission and a GPU watchdog guard.
- Diffusion blur that is exact and the same in Viewer and export; halation highlight boost measured from the brightest value; scanner white and black levels that keep mid-grey.
- Emulsion aging, film and paper base density, six additional colour-film profiles appended without changing existing profile indices.
- With the new options off, output is bit-identical to the renderer before each change; the tests in `tests/` check this.

## Python reference (`upstream-spektrafilm`)

A source subset without examples, screenshots and unrelated tests. The profile reader accepts the schema additions of the six added profiles. It is used only for the optional LUT export and as a reference in tests.
