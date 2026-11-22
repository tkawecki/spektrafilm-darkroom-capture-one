using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

internal enum CollisionAction { Rename, Overwrite, Skip }
internal enum PlannedOutcome { New, Renamed, Replaces, Skipped, NotSupported }

/// <summary>One requested output: a variant written through one recipe, at the path Capture One's naming gives.</summary>
internal sealed record PlanRequest(Guid VariantId, string SourcePath, string RecipeName, string DesiredPath,
    CollisionAction Collision, string? NotSupported = null);

/// <summary>Where the output goes and what happens there. Note explains a rename or skip to the user.</summary>
internal sealed record PlannedFile(Guid VariantId, string SourcePath, string RecipeName, string DesiredPath,
    string OutputPath, PlannedOutcome Outcome, string? Note = null)
{
    internal bool Writes => Outcome is PlannedOutcome.New or PlannedOutcome.Renamed or PlannedOutcome.Replaces;
}

/// <summary>Recipe export decisions that need no Capture One: collisions, protection of originals, colour space, XMP.</summary>
internal static class RecipeExportPlanning
{
    internal const string XmpNamespace = "https://github.com/tkawecki/spektrafilm-darkroom-capture-one/ns/look/1.0/";

    internal static CollisionAction CollisionFrom(string? recipeAction) => recipeAction switch
    {
        "Overwrite" => CollisionAction.Overwrite,
        "Skip" => CollisionAction.Skip,
        _ => CollisionAction.Rename,
    };

    /// <summary>
    /// Resolves every request against the files on disk and against each other, in order. An output never
    /// replaces an original being exported or another output of the same export: those are renamed like
    /// Capture One renames (name_1, name_2, …), whatever the recipe says.
    /// </summary>
    internal static IReadOnlyList<PlannedFile> Resolve(IEnumerable<PlanRequest> requests, IEnumerable<string> originals,
        Func<string, bool> exists, Func<string, string, bool>? sameFile = null)
    {
        sameFile ??= (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var protectedSources = originals.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Next to the originals there may be other images of the catalog (a JPEG beside its RAW): never replaced.
        var originalFolders = protectedSources.Select(Path.GetDirectoryName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PlannedFile>();
        bool IsOriginal(string path) => protectedSources.Any(source => sameFile(path, source));
        bool Taken(string path) => claimed.Contains(path) || exists(path) || IsOriginal(path);
        foreach (var request in requests)
        {
            var desired = Path.GetFullPath(request.DesiredPath);
            if (request.NotSupported is { } reason)
            {
                result.Add(new PlannedFile(request.VariantId, request.SourcePath, request.RecipeName, desired, desired, PlannedOutcome.NotSupported, reason));
                continue;
            }
            PlannedFile planned;
            if (IsOriginal(desired))
                planned = Renamed(request, desired, "the recipe would replace the original image");
            else if (claimed.Contains(desired))
                planned = request.Collision == CollisionAction.Skip
                    ? new PlannedFile(request.VariantId, request.SourcePath, request.RecipeName, desired, desired, PlannedOutcome.Skipped, "another file of this export has this name")
                    : Renamed(request, desired, "another file of this export has the same name");
            else if (!exists(desired))
                planned = new PlannedFile(request.VariantId, request.SourcePath, request.RecipeName, desired, desired, PlannedOutcome.New);
            else
                planned = request.Collision switch
                {
                    CollisionAction.Overwrite when originalFolders.Contains(Path.GetDirectoryName(desired) ?? string.Empty)
                        => Renamed(request, desired, "files in the folder of the originals are never replaced"),
                    CollisionAction.Overwrite => new PlannedFile(request.VariantId, request.SourcePath, request.RecipeName, desired, desired, PlannedOutcome.Replaces),
                    CollisionAction.Skip => new PlannedFile(request.VariantId, request.SourcePath, request.RecipeName, desired, desired, PlannedOutcome.Skipped, "a file with this name exists"),
                    _ => Renamed(request, desired, null),
                };
            if (planned.Writes) claimed.Add(planned.OutputPath);
            result.Add(planned);
        }
        return result.AsReadOnly();

        PlannedFile Renamed(PlanRequest request, string desired, string? note) =>
            new(request.VariantId, request.SourcePath, request.RecipeName, desired, FreeName(desired, Taken), PlannedOutcome.Renamed, note);
    }

    /// <summary>Capture One's pattern: name, then name_1, name_2, … until the name is free.</summary>
    internal static string FreeName(string desired, Func<string, bool> taken)
    {
        if (!taken(desired)) return desired;
        var directory = Path.GetDirectoryName(desired) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        for (var index = 1; index < 100_000; index++)
        {
            var candidate = Path.Combine(directory, stem + "_" + index.ToString(CultureInfo.InvariantCulture) + extension);
            if (!taken(candidate)) return candidate;
        }
        throw new IOException("Too many files named " + Path.GetFileName(desired) + ".");
    }

    /// <summary>sRGB and Adobe RGB renders pass through Capture One unchanged; other profiles get a ProPhoto render it converts.</summary>
    internal static ExportRgbSpace RenderSpaceForProfile(string? profileName, string? profilePath)
    {
        // The path names the file the recipe really uses; the display name is a fallback.
        var name = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(profilePath) ? profileName ?? string.Empty : profilePath).Trim();
        if (name.Equals("sRGB Color Space Profile", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sRGB IEC61966-2.1", StringComparison.OrdinalIgnoreCase)) return ExportRgbSpace.Srgb;
        if (name.Equals("Adobe RGB (1998)", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AdobeRGB1998", StringComparison.OrdinalIgnoreCase)) return ExportRgbSpace.AdobeRgb;
        return ExportRgbSpace.ProPhotoRgb;
    }

    /// <summary>Description of the Spektrafilm look written into the file's XMP.</summary>
    internal sealed record LookDescription(string Film, string Paper, string Mode, double FilmExposure, double PrintExposure,
        double Strength, string? Version, string Software);

    /// <summary>
    /// Adds the look as its own XMP description to the packet Capture One wrote. An empty packet becomes a minimal one.
    /// Values are escaped; Capture One's own properties are left as they are.
    /// </summary>
    internal static string AddLook(string xmp, LookDescription look)
    {
        var block = new StringBuilder()
            .Append("<rdf:Description rdf:about=\"\" xmlns:spektrafilm=\"").Append(XmpNamespace).Append("\">")
            .Append(Property("Software", look.Software))
            .Append(Property("FilmStock", look.Film))
            .Append(Property("PrintStock", look.Paper))
            .Append(Property("ProcessingMode", look.Mode))
            .Append(Property("FilmExposure", Number(look.FilmExposure)))
            .Append(Property("PrintExposure", Number(look.PrintExposure)))
            .Append(Property("LookStrength", Number(look.Strength)));
        if (!string.IsNullOrWhiteSpace(look.Version)) block.Append(Property("LookVersion", look.Version));
        block.Append("</rdf:Description>");
        const string end = "</rdf:RDF>";
        var at = xmp.LastIndexOf(end, StringComparison.Ordinal);
        if (at >= 0) return xmp.Insert(at, block.ToString());
        return "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + block + end + "</x:xmpmeta>";

        static string Property(string name, string value) =>
            $"<spektrafilm:{name}>{SecurityElement.Escape(value)}</spektrafilm:{name}>";
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
