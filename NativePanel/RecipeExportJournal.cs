using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal enum JournalState { Pending, Done, Skipped, Failed }

/// <summary>One planned output of a recipe export and how far it got.</summary>
internal sealed record JournalItem(Guid VariantId, string SourcePath, string RecipeName, string OutputPath,
    PlannedOutcome Outcome, JournalState State = JournalState.Pending, string? Temporary = null, string? Error = null);

/// <summary>A recipe export of one catalog, kept on disk until it completes so an interrupted export can be resumed.</summary>
internal sealed record ExportJournal(int Version, Guid Id, string Catalog, DateTime CreatedUtc, IReadOnlyList<JournalItem> Items)
{
    internal const int CurrentVersion = 1;
    internal IEnumerable<JournalItem> Remaining => Items.Where(item => item.State is JournalState.Pending or JournalState.Failed);
    internal int RemainingCount => Remaining.Count();
    internal bool Finished => RemainingCount == 0;

    internal ExportJournal With(int index, JournalItem item)
    {
        var items = Items.ToArray();
        items[index] = item;
        return this with { Items = Array.AsReadOnly(items) };
    }
}

/// <summary>
/// Stores at most one interrupted export per catalog, next to panel.log ("Interrupted exports"). Without the
/// launcher's log variable (tests) nothing is written. A damaged or newer journal is ignored, never overwritten.
/// </summary>
internal static class RecipeExportJournal
{
    internal static string? Directory
    {
        get
        {
            var log = Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG");
            return string.IsNullOrWhiteSpace(log) ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(log))!, "Interrupted exports");
        }
    }

    internal static string FileName(string catalog) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(catalog).ToUpperInvariant())))[..24] + ".json";

    internal static string? PathFor(string catalog, string? directory = null) =>
        (directory ?? Directory) is { } folder ? Path.Combine(folder, FileName(catalog)) : null;

    internal static ExportJournal? Load(string catalog, string? directory = null)
    {
        if (PathFor(catalog, directory) is not { } path || !File.Exists(path)) return null;
        try
        {
            var journal = PanelFiles.ReadPreset<ExportJournal>(path);
            if (journal.Version != ExportJournal.CurrentVersion || journal.Items is null
                || !string.Equals(Path.GetFullPath(journal.Catalog), Path.GetFullPath(catalog), StringComparison.OrdinalIgnoreCase))
                return null;
            return journal.Finished ? null : journal;
        }
        catch (Exception error)
        {
            StartupHook.Log("An interrupted export could not be read: " + error.GetBaseException().Message);
            return null;
        }
    }

    internal static void Save(ExportJournal journal, string? directory = null)
    {
        if (PathFor(journal.Catalog, directory) is not { } path) return;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PanelFiles.WritePreset(path, journal);
    }

    internal static void Delete(string catalog, string? directory = null)
    {
        if (PathFor(catalog, directory) is { } path && File.Exists(path)) File.Delete(path);
    }

    /// <summary>Removes a temporary output an interrupted export left next to its destination.</summary>
    internal static void RemoveLeftovers(ExportJournal journal)
    {
        foreach (var item in journal.Items)
            if (item.Temporary is { } temporary && Path.GetFileName(temporary).StartsWith(".spektrafilm-", StringComparison.Ordinal))
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
