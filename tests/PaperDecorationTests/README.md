# Paper decoration tests

Run from the repository root (does not build/start Capture One or the shared panel):

```powershell
dotnet run --project tests/PaperDecorationTests -c Release
```

`python tests/PaperDecorationTests/generate_fixtures.py` regenerates 158 cases from the authenticated installed reference implementation 2.2.3 source. It executes unchanged `paper_texture.py` and the actual `apply_paper_finish_to_export`, `apply_border_padding`, `apply_white_padding`, and `padding_pixels_for_image` function ASTs with small test doubles for their GUI context. It does not import Qt, initialize a GPU, or run an application. Source hashes and NumPy version are recorded in `fixtures.json`.

Coverage includes all six modes; opacity 0/.35/1; ordered multi-layer composites; both texture/image aspect orientations; 1-pixel inputs; exact linspace/floor nearest fitting; padding, no padding, inactive and empty states; unbounded input values with the deliberate active blend clamp; immutable neutral memory; source alpha; cancellation; dimension guards; settings serialization/cache identity; independent per-look dictionaries; malformed saved data; normalized RGB/RGBA texture imports; deduplication across containers/alpha; hash validation; bounded decoding; caching; original-file independence; and actual RGBA16 PNG export with unchanged ICC and more-than-8-bit photograph codes.

2026-11-16: **60,481 assertions PASS**, maximum float32 error against reference **0** across 59,960 RGBA oracle samples. Artifact proof: `artifacts/full-function-20261116/paper-decoration/ffb01b24dd704016bcdb507f39a975da/verification.json`. The test writes only synthetic images beneath its own artifact directory and removes only the temporary source/oversize files it created. It deliberately corrupts one isolated test-store asset to verify tamper detection.
