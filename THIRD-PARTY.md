# Third-party notices

Spektrafilm Darkroom for Capture One is an unofficial project for a separately installed copy of Capture One. It is not affiliated with Capture One A/S or with the authors of the projects below. The title names the film emulation engine and the host application; trademark rights are outside the scope of the code and data licences below.

## Spektrafilm for Capture One (the original integration)

This project is a modified version of [Spektrafilm for Capture One](https://github.com/nothing-complex/spektrafilm-for-capture-one) by [Luka Kauzlaric](https://www.lukakauzlaric.com), GNU GPL version 3: the Capture One panel, the launcher, the installer and the still-renderer wrapper started from its branch `experimental/unchecked-capture-one` at commit `0607311` (2026-11-14). Later fixes were ported from its commit `285f0aa`. The changes are summarised in [UPSTREAM-CHANGES.md](UPSTREAM-CHANGES.md). If you use Capture One 16.4, use the original project.

## spektrafilm OFX

The Vulkan renderer is built from Aedan Diez’s public [spektrafilm OFX](https://spektrafilm.114c.de/) source ([repository](https://github.com/chaert-s/spektrafilm-ofx)) at commit `86476afc5b077de77e2278e3658d1ba9309892a1`, with the changes recorded in [UPSTREAM-CHANGES.md](UPSTREAM-CHANGES.md). The [OFX source license](upstream-ofx/LICENSE.txt) is GNU GPL version 3. The renderer executable, compiled shaders, and runtime tables in the Windows release are built from the corresponding source and public inputs in this repository. The bundled OFX stock files retain their individual credits and license notices. See the [OFX source](https://github.com/chaert-s/spektrafilm-ofx/tree/86476afc5b077de77e2278e3658d1ba9309892a1) and [upstream third-party notices](upstream-ofx/Legal/THIRD_PARTY_NOTICES.txt).

The upstream [official binary notice](upstream-ofx/Legal/SPEKTRAFILM_OFX_LICENSE.txt) and [OFX-exported LUT license](upstream-ofx/Legal/SPEKTRAFILM_OFX_LUT_LICENSE.txt) are kept for reference. They cover upstream official packages and their exported LUTs. This project builds its own renderer. The package omits the official OFX bundles, their presets, and the licensed SMPTE ST 2065-2 data required by Academy Printer Density.

The spectral reconstruction includes Johannes Hanika’s Hanatos 2025 work and an alternate reconstruction based on Mallett and Yuksel’s 2019 paper. The upstream notices link to the original work. [vkdt](https://github.com/hanatos/vkdt) · [Mallett and Yuksel](https://diglib.eg.org/items/bbffa865-e99c-4c1f-bd33-70102dc8af78)

## spektrafilm Python reference

The optional LUT worker uses the GPLv3 Python reference implementation by Andrea Volpato at commit `3bb2c2d2801ff68b92019cf1dbcbb133d60832bc`. The upstream [source license](upstream-spektrafilm/LICENSE), [citation metadata](upstream-spektrafilm/CITATION.cff), and [profile and LUT license](upstream-spektrafilm/SPEKTRAFILM_LICENSE.txt) accompany it. Its film profile JSONs and direct derivatives, including `.cube` files exported by this project’s optional Python worker, carry CC BY-SA 4.0 attribution to Andrea Volpato. If you share an exported LUT, retain its attribution, source and license links, and indicate changes. [spektrafilm reference source](https://github.com/andreavolpato/spektrafilm/tree/3bb2c2d2801ff68b92019cf1dbcbb133d60832bc) · [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)

## Parts adapted from The Dyeworks 2.2.3

Some parts are adapted from The Dyeworks 2.2.3 by Celian Le Bolloch, a GPL-3 fork of Andrea Volpato's spektrafilm ([source](https://github.com/natcelian/spektrafilm.exe)). Code: GPL-3.0; profile data: CC BY-SA 4.0. Each adapted source file names its origin in its header.

- **Profiles:** six colour-film profiles (Eterna 250D, Velvia 100F, Ultramax 800, Vision2 100T/500T/50D) and their TH-KG3 print-filter entries, with their metadata unchanged.
- **Renderer:** RMS grain (`SpektraGrainRms.h`, GPL-3.0-or-later), emulsion aging (`SpektraFilmAging.h`), film and paper base density (`SpektraBaseDensity.h`).
- **Panel:** scan finishing and micro contrast (`Finishing.cs`, `FinishingMicroContrast.cs`), scan geometry (`ScanGeometry.cs`), paper textures and border (`PaperDecoration.cs`), Hald/LUT layout (`ColorLutFiles.cs`), linear DNG (`LinearDngWriter.cs`), exposure strips (`ExposureComparisonModel.cs`) and the export colour-space list.

## Photos

- The panel screenshot (`assets/screenshot.jpg`, Quick start) and the photo of a village with white houses and palms in the user manual are the project author's own photos: © the author, all rights reserved. They are not covered by the GPL or CC BY-SA licences of this repository and may not be reused without the author's permission.
- The other example photos in the user manual are free photos released under CC0 1.0 on discuss.pixls.us: two photos of a heath by Kees Guequierre ([PlayRaw] Heath) and the arcades in Rome by the user shown as anon42681393. Sources are listed in the manual's chapter "Sources and licences".

## External dependencies

Builds use a locally installed Vulkan SDK and Visual Studio C++ toolchain. The panel builds against managed assemblies in the user’s Capture One installation. The distribution excludes those assemblies, Capture One SDK files, and Capture One camera ICC templates. The renderer uses the Vulkan loader supplied by the installed graphics drivers. The optional Python setup obtains Python packages from their package index. Those packages retain their own licenses and notices. No Python wheels, LLVM tools, standalone Vulkan SDK files, or Capture One components are redistributed here.
