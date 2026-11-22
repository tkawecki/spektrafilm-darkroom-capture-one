using System;
using System.IO;
using System.Linq;

// The legacy class name is kept for the existing callers and single-catalog launcher.
internal static class ApprovedCatalog
{
    private static bool AllDocuments => Environment.GetEnvironmentVariable("SPEKTRAFILM_DOCUMENT_MODE") == "all";
    internal static string? SelectedDatabase => ResolveDatabase(Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG"));
    internal static bool HasSelection => AllDocuments || SelectedDatabase is not null;
    internal static bool IsApproved(string? activePath) => ApprovedDatabase(activePath) is not null;

    internal static string? ApprovedDatabase(string? activePath)
    {
        var database = ResolveDatabase(activePath);
        if (database is null || (!AllDocuments && !Matches(database, SelectedDatabase)))
            return null;
        // Optional launch-scoped restriction used while testing ONLY copies.
        var testRoot = Environment.GetEnvironmentVariable("SPEKTRAFILM_DOCUMENT_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            if (!Path.IsPathFullyQualified(testRoot)) return null;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot)) + Path.DirectorySeparatorChar;
            if (!database.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            // Do not allow a junction inside the test tree to point at an original document.
            for (var entry = new FileInfo(database) as FileSystemInfo; entry is not null;)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent;
            }
        }
        return database;
    }

    internal static string? ResolveDatabase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (File.Exists(path)) return IsDatabase(path) ? path : null;
            if (!Directory.Exists(path)) return null;
            var databases = Directory.EnumerateFiles(path).Where(IsDatabase).Take(2).ToArray();
            return databases.Length == 1 ? Path.GetFullPath(databases[0]) : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return null; }
    }
    private static bool IsDatabase(string path) => Matches(Path.GetExtension(path), ".cocatalogdb") || Matches(Path.GetExtension(path), ".cosessiondb");
    private static bool Matches(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
