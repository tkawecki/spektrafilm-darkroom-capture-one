# Diagnostyka Grain Synthesis

Test obserwuje rzeczywisty shader Vulkan na syntetycznych płaskich polach. Uruchamia wyłącznie kopię samodzielnego renderera, bez C1, zdjęć i zapisanych wyglądów. Instrumentacja kopii `SpektraPrintScan.comp` zwraca gęstość wejściową druku; warianty kopii `SpektraGrain.comp` odsłaniają gęstości docelowe warstw i zmierzone pokrycie.

Wymagania: Python z NumPy, zbudowany renderer z zasobami oraz `glslang.exe` albo `glslangValidator.exe`. Przykład z katalogu repo:

```powershell
python tests/GrainDiagnostics/grain_diagnostics.py `
  --renderer build/full-export/Release/SpektraStillRenderer.exe `
  --glslang build/grain-fix/glslang/bin/glslang.exe
```

Powstaje nowy ignorowany katalog `artifacts/grain-diagnostics-<data>` z JSON, tablicami NumPy i kopią runtime. Istniejący katalog docelowy jest odrzucany. Test sprawdza hashe oryginalnego renderera, shaderów źródłowych i dwóch instrumentowanych zasobów przed/po. Nie wdraża plików. Na końcu przywraca w kopii oryginalne zasoby SPIR-V; plików `.comp` z katalogu wyników nie należy używać produkcyjnie.

`PASS` oznacza potwierdzenie matematycznego mechanizmu oraz zachowanie oryginałów, **nie naprawę przesunięcia tonacji**. Test porównuje pokrycie z `1 - 10^-D`, izoluje próbki i cell cap, warstwowanie i skale promieni RGB. Przy aperturze zero każde wyjście jest zerem albo gęstością ograniczoną epsilon: średnia gęstość odpowiada `(1 - 10^-D) * (-log10(epsilon))`, choć średnia transmisja nadal jest bliska `10^-D`.

Wyniki z 16 listopada 2026 pokazują, że nieliniowa konwersja przestrzennie zmiennego pokrycia do gęstości znacząco zmienia jej średnią. Powiększanie próbek redukuje błąd Monte Carlo, ale nie usuwa składnika związanego z aperturą i wielkością ziaren. Zmiana apertury lub recentering gęstości zmieniają model i zapisany wygląd; nie są neutralną poprawką jasności.

Referencje: [opis OFX](https://spektrafilm.114c.de/technical/) nazywa Synthesis eksperymentalnym modelem; [Newson i in. 2017](https://www.ipol.im/pub/art/2017/192/), §3.2, definiuje filtrowane pokrycie bezpośrednio jako obraz i sigma w jednostkach piksela wyjściowego. OFX stosuje dodatkową konwersję na CMY i spektralny Print/Scan, dlatego te testy nie dowodzą pełnej zgodności z artykułem ani rzeczywistym filmem.
