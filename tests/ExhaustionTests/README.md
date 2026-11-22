# Paper developer exhaustion

The C1 worker accepts `--print-exhaustion 0..1`, backed by
`RenderParams::printDeveloperExhaustion` (default zero). Only Print applies it;
Scan and Process Negative ignore retained paper-processing values. The panel
must pass the saved value without changing the document/variant data schema.

The CPU helper implements the pinned Python `utils/morph_curves.py` fitted
three-layer normal/Gumbel mixture, coupled gamma, sigma floor and per-channel
center compensation. Fitted model parameters come from the eight pinned Python
paper JSONs, with SHA256 and Andrea Volpato/CC BY-SA attribution in the generated
header. Regenerate with `python upstream-ofx/tools/generate_print_curve_models.py`.
This does not rewrite the legacy native spectral/profile resource tables.

Native legacy paper samples differ from the newer fitted profiles. Replacing
them at the first nonzero slider value would change existing looks abruptly;
adding their residuals produced new negative densities for Kodak 2393. The
implementation therefore maps the fitted morphed density through the inverse
fitted baseline, then samples the legacy curve at that exposure. The inverse
uses the smaller CDF tail to avoid numerical cancellation near saturation.
The finite LUT's first/last density samples remain exactly the legacy endpoints.
The same legacy exposure grid is retained; no export resolution or quality
setting changes. This is the pinned morph adapted to the existing native paper,
not a claim of absolute pixel equivalence with the newer Python profiles.

At exhaustion zero the existing upload path and shader gamma stay unchanged.
Nonzero curve generation compensates for the shader's exposure-times-gamma
lookup, applying gamma and print push/pull once. Cache keys include paper,
exhaustion and effective gamma; returning to zero restores the original buffer.

Analytical D(0), layer amplitudes and asymptotic fitted Dmax are preserved.
The existing 256-sample LUT does not contain an exact zero-exposure knot; its
interpolated D(0) can differ from the analytical value by up to 0.000915 density
in the tested matrix. The legacy samples themselves have small negative values
and tail ripples (about 0.001 density); the warp preserves their range, and
tests verify it adds no total negative variation. It does not silently repair
old profile data.

Build/test on synthetic input without opening C1:

```powershell
cmake -S tests/ExhaustionTests -B build/exhaustion/tests -A x64
cmake --build build/exhaustion/tests --config Release --parallel 4
python tests/ExhaustionTests/test_reference.py --probe build/exhaustion/tests/Release/PrintCurveProbe.exe
cmake -S ofx-still-renderer -B build/exhaustion -A x64 -DOFX_BUILD=build/native40/resources -DVulkan_INCLUDE_DIR=build/native40/deps/Vulkan-Headers-1.4.328/include -DVulkan_LIBRARY=build/native40/deps/vulkan-1.lib
cmake --build build/exhaustion --config Release --parallel 4
python tests/ExhaustionTests/test_renderer.py --exe build/exhaustion/Release/SpektraStillRenderer.exe --baseline build/full-export/Release/SpektraStillRenderer.exe
python ofx-still-renderer/smoke.py --exe build/exhaustion/Release/SpektraStillRenderer.exe
```

The reference test loads the real pinned `io.py` and `morph_curves.py` modules
without starting the unrelated photo runtime; it needs numpy/scipy, not colour.
It covers 240 combinations: eight papers, five effective gamma values spanning
the supported range and six exhaustion values. Fitted density tolerance is
2e-8; observed maximum error was 2.52e-10. Native inverse-warp tolerance is 8e-7.
The Vulkan test covers all papers, combined gamma/push, exact zero parity with
the previous worker, sub-quantization near-zero changes, alpha/finite values,
REUSE invalidation, Scan invariance and invalid CLI bounds. The original red
test failed against the old worker with `Unknown setting: --print-exhaustion`.

These tests do not replace the parent task's panel build, copied-session C1
verification and deployment backup. No user document, photo or runtime is
written by these tests.
