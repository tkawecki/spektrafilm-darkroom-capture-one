# RecipeExportTests

Run from the repository with .NET 10:

```powershell
dotnet run --project tests/RecipeExportTests -c Release
```

Checks the parts of the export through Capture One's recipes that need no running Capture One, by reflection on
the production panel assembly: free names (`name_1`, `name_2`, as Capture One names them), the recipe collision
rules (rename, replace, skip), that an original or a file in the originals' folder is never replaced, that two
files of one export never share a name, the render space chosen for a recipe profile, the look description added to
Capture One's XMP, detection and repair of byte-swapped 16-bit PNG (all row filters, several image-data chunks,
other chunks kept), the export journal (resume, foreign catalogs, leftover temporary files) and publishing finished
files. Test files go to a new folder under the ignored `artifacts/recipe-export-tests`.

The rest is checked inside Capture One on a copied session with a separate test hook that is not published:
`recipe-export`, `recipe-watermark`, `recipe-native` and `recipe-gaps` (counters in file names, output sharpening,
image watermark, PSB, linear DNG, a file above 50 MP, a variant with Spektrafilm off, a folder that refuses files)
and `recipe-roots` (image folder and output location as destinations), run on a copied session and a copied catalog.
