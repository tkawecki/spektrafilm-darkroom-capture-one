# Viewer pack tests

The renderer blends Look strength below 100% when it packs Viewer pixels (`--display-strength`, `ofx-still-renderer/main.cpp` `DisplayBlend`). This test checks that the result is byte-identical to the panel's managed conversion of the float frame (`SpatialPreview.DecodeDecoratedPreview`).

It covers four input spaces, four strengths, and two frame sizes: pipe transport and shared-memory transport.

~~~powershell
& $dotnet run --project tests/ViewerPackTests -c Release -- <NativePanel.dll> <runtime-with-renderer> <source.f32> <width> <height>
~~~

A source is any C1-developed float RGBA frame, e.g. `*.source.f32` from a copy-only benchmark.
