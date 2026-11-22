# Additional image-export profiles

These eleven generated ICC files describe the new linear/SDR export pairs. `make_export_profiles.py` reproduces them from standard primaries, Bradford adaptation and transfer functions using Colour 0.4.7. `spaces.json` records gamut/encoding identity, both matrices to/from linear sRGB, file names and SHA256 hashes. Its first four records retain their mathematical references but have no local ICC filename because those historical profiles remain in their original OS/C1/`profiles/preview` locations.

The image-export registry matches the nine choices in reference implementation 2.2.3 `spektrafilm_gui/options.py::RGBColorSpaces`, each encoded or linear. ACES2065-1 is linear under both flags, giving seventeen unique pairs. This is separate from the much larger camera-log registry used for LUT exports.

All new ICC files are matrix/TRC RGB profiles with D50 PCS. Source white points remain distinct: D65 for standard display/wide gamuts, the DCI white for DCI-P3, D60 for ACES AP0, and the existing project ICC D50 convention for ProPhoto. The four previous IDs, enum values, conversion arithmetic and profile bytes remain unchanged.

Encoded DaVinci Wide Gamut/Intermediate and V-Gamut/V-Log deliberately have **no embedded ICC**. They retain exact unbounded log conversion and explicit gamut/transfer metadata. This agrees with the upstream image exporter, which has no profiles for those pairs; attaching an sRGB profile or silently normalizing their scene highlights would misdescribe the pixels. Ordinary ICC XYZ PCS encoding is bounded and is not a lossless declaration of unrestricted scene-log latitude; see the [ICC v4 specification](https://www.color.org/specifications/ICC.1-2022-05.pdf).

BT.2020 uses the nominal rounded OETF constants (alpha 1.099, beta .018) in pixel conversion. The corresponding inverse has a small downward discontinuity at OETF(.018). Its ICC TRC is a 4096-entry, 16-bit monotonic upper-envelope table instead of encoding that reversal. The local linear-light deviation is approximately 5.5e-5 near the discontinuity; pixel conversion remains the exact nominal formula. Other curves use ICC parametric TRCs or identity. The profiles are checked against independent Colour D50 PCS and LittleCMS, not inferred from their labels.

The runtime resolver validates bounded ICC structure, RGB/XYZ metadata and each generated file hash. Include this entire directory when assembling a runtime. Never replace the original four profiles with these files or assign these working-space profiles as monitor profiles.
