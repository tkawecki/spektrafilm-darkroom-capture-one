using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed record GrainControls(int Type, double Impact, double Granularity);
internal sealed record VariantLook(LookSettings Look, SpatialPreview.Settings Optics, bool Enabled, int Version = 1, GrainControls? Grain = null);

// Data-safety ideas ported from nothing-complex/spektrafilm-for-capture-one 285f0aa (HS-03/HS-04, GPL-3.0),
// simplified for this fork. The saved look file format is unchanged.
//  * An existing look that cannot be read is copied to "Preserved originals" before it is first overwritten.
//  * When a save fails, the edit is also written to a recovery checkpoint, so closing C1 does not lose it.
//    The checkpoint is offered again on the next read if it is newer than the saved look.
//  * Members written by another panel version are kept when this panel saves the look (UnknownLookFields),
//    so rolling the DLL back and editing does not erase newer settings.
internal sealed class VariantLookStore
{
    // Written in each catalog's look folder: the catalog it belongs to, so a folder left behind by a catalog that was
    // moved or renamed can be recognised (after upstream 14f1d5b, Spektrafilm for Capture One, Luka Kauzlaric, GPL-3.0).
    private const string MarkerName = "catalog.json";
    private sealed record CatalogMarker(string Database);

    private readonly string root;
    private readonly string database;
    private readonly string directory;
    private readonly string recoveryDirectory;
    // Variants whose edit reached neither the look file nor a recovery checkpoint: only in memory.
    private readonly HashSet<Guid> unprotected = new();
    private readonly Dictionary<Guid, VariantLook> pending = new();
    // Variants whose saved file (or its absence) was read successfully, so it may be overwritten.
    private readonly HashSet<Guid> verified = new();
    private readonly HashSet<Guid> unloadable = new();

    internal VariantLookStore(string root, string catalogDatabase)
    {
        var key = CatalogKey(catalogDatabase);
        var fullRoot = Path.GetFullPath(root);
        this.root = Path.TrimEndingDirectorySeparator(fullRoot);
        database = Path.GetFullPath(catalogDatabase);
        directory = Path.Combine(fullRoot, key);
        recoveryDirectory = Path.Combine(Path.GetDirectoryName(fullRoot.TrimEnd(Path.DirectorySeparatorChar))!, "Look recovery", key);
        if (Directory.Exists(directory))
            WriteMarker();
    }

    /// <summary>Where "Restore grain" keeps what the panel applied to Capture One's Grain, per variant.</summary>
    internal string GrainRecords => Path.Combine(directory, "Grain");

    /// <summary>True when an edit could be saved neither to its look file nor to a recovery checkpoint.</summary>
    internal bool HasUnprotectedPending => unprotected.Overlaps(pending.Keys);

    /// <summary>
    /// The look folder of a catalog that was moved or renamed and holds a saved look of this variant, when this
    /// catalog has none. Variant IDs are unique, so such a folder belongs to this catalog (or to a copy of it whose
    /// file no longer exists where it was).
    /// </summary>
    internal string? MovedLooksFolder(Guid variant)
    {
        var candidates = orphans ??= FindOrphanFolders();
        if (candidates.Count == 0 || File.Exists(GetPath(variant)))
            return null;
        var name = variant.ToString("N") + ".json";
        return candidates.FirstOrDefault(folder => File.Exists(Path.Combine(folder, name)));
    }

    // Look folders of other catalogs whose catalog file is gone (or that were saved before folders were marked; the
    // user decides). Found once per store, so selecting photos does not scan the folders each time.
    private IReadOnlyList<string>? orphans;
    private IReadOnlyList<string> FindOrphanFolders()
    {
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(root))
                return found;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(Path.GetFullPath(folder), directory, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var marker = Path.Combine(folder, MarkerName);
                if (!File.Exists(marker))
                {
                    found.Add(folder);
                    continue;
                }
                try
                {
                    var owner = PanelFiles.ReadPreset<CatalogMarker>(marker).Database;
                    if (!string.IsNullOrWhiteSpace(owner) && !File.Exists(owner))
                        found.Add(folder);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupHook.Log("Spektrafilm could not look for the looks of a moved catalog: " + error.Message);
        }
        return found;
    }

    /// <summary>
    /// Copies the looks, versions and grain records of a moved catalog's folder into this catalog's folder. The old
    /// folder is left as it was; looks this catalog already has are not replaced. Returns how many looks were copied.
    /// </summary>
    internal int CopyLooksFrom(string folder)
    {
        var source = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar)), root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That folder is not another catalog's look folder.");
        Directory.CreateDirectory(directory);
        var copied = 0;
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(source, "*", walk))
        {
            var relative = Path.GetRelativePath(source, file);
            if (string.Equals(relative, MarkerName, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).StartsWith(".spektra-", StringComparison.Ordinal))
                continue;
            var target = Path.Combine(directory, relative);
            if (File.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            if (Path.GetDirectoryName(relative) is null or "" && relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                copied++;
        }
        WriteMarker();
        orphans = null;
        StartupHook.Log($"Copied {copied} Spektrafilm looks from {source} to {directory}.");
        return copied;
    }

    private void WriteMarker()
    {
        try
        {
            var path = Path.Combine(directory, MarkerName);
            if (File.Exists(path) && string.Equals(PanelFiles.ReadPreset<CatalogMarker>(path).Database, database, StringComparison.OrdinalIgnoreCase))
                return;
            PanelFiles.WritePreset(path, new CatalogMarker(database));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            StartupHook.Log("Spektrafilm could not mark its look folder: " + error.Message);
        }
    }

    /// <summary>Set by <see cref="Read"/> when an unsaved edit was recovered from a checkpoint.</summary>
    internal string? RecoveryNotice { get; private set; }

    internal VariantLook? Read(Guid variant)
    {
        RecoveryNotice = null;
        if (pending.TryGetValue(variant, out var edit))
            return edit;
        var primary = ReadFile(GetPath(variant));
        verified.Add(variant);
        return RecoverPending(variant, primary) ?? primary;
    }

    internal void Queue(Guid variant, VariantLook look) => pending[variant] = look;

    internal void Flush()
    {
        if (pending.Count == 0)
            return;
        var failures = new List<string>();
        foreach (var (variant, look) in pending.ToArray())
        {
            JsonObject? json = null;
            try
            {
                var created = !Directory.Exists(directory);
                Directory.CreateDirectory(directory);
                if (created) WriteMarker();
                json = ToJson(variant, look); // Before PreserveUnverified clears the unloadable mark.
                PreserveUnverified(variant);
                var started = PreviewPerformance.Start();
                PanelFiles.WritePreset(GetPath(variant), json);
                PreviewPerformance.End(started, "settings.save");
                pending.Remove(variant);
                unprotected.Remove(variant);
                verified.Add(variant);
                TryDelete(GetRecoveryPath(variant));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                var (message, isProtected) = WriteRecovery(variant, json ?? JsonSerializer.SerializeToNode(look)!.AsObject(), error);
                if (isProtected) unprotected.Remove(variant);
                else unprotected.Add(variant);
                failures.Add(message);
            }
        }
        if (failures.Count != 0)
            throw new IOException(string.Join(Environment.NewLine, failures));
    }

    private string GetPath(Guid variant) => Path.Combine(directory, variant.ToString("N") + ".json");
    private string GetRecoveryPath(Guid variant) => Path.Combine(recoveryDirectory, variant.ToString("N") + ".pending.json");
    internal string VersionsFolder(Guid variant) => Path.Combine(directory, "Versions", variant.ToString("N"));

    private static VariantLook? ReadFile(string path)
    {
        try
        {
            var saved = PanelFiles.ReadPreset<VariantLook>(path);
            if (saved.Version != 1 || saved.Look is null || saved.Optics is null)
                throw new InvalidDataException("The saved Spektrafilm look has an unsupported format.");
            return saved;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // A checkpoint newer than the saved look is an edit that never reached the normal file.
    private VariantLook? RecoverPending(Guid variant, VariantLook? primary)
    {
        var recoveryPath = GetRecoveryPath(variant);
        if (!File.Exists(recoveryPath))
            return null;
        VariantLook? recovered;
        try { recovered = ReadFile(recoveryPath); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            Archive(recoveryPath, variant, "unreadable-recovery");
            TryDelete(recoveryPath);
            return null;
        }
        var primaryPath = GetPath(variant);
        if (recovered is null || (primary is not null && Serialize(primary) == Serialize(recovered)))
        {
            TryDelete(recoveryPath); // The edit was saved after all; only cleanup had failed.
            return null;
        }
        if (primary is not null && File.GetLastWriteTimeUtc(primaryPath) >= File.GetLastWriteTimeUtc(recoveryPath))
        {
            // The normal file was saved later (for example from another session). Keep the older edit aside.
            Archive(recoveryPath, variant, "superseded-recovery");
            TryDelete(recoveryPath);
            return null;
        }
        pending[variant] = recovered;
        RecoveryNotice = "Recovered this image's last unsaved Spektrafilm look. It will be saved again with your next change.";
        StartupHook.Log(RecoveryNotice + " " + recoveryPath);
        return recovered;
    }

    /// <summary>
    /// Marks a look whose file reads but cannot be applied (for example a missing stock or imported
    /// dependency). Its file is copied aside before anything replaces it.
    /// </summary>
    internal void ProtectUnloadable(Guid variant) => unloadable.Add(variant);

    // Never replace a saved look that could not be read or applied without keeping a copy of it first.
    private void PreserveUnverified(Guid variant)
    {
        if (verified.Contains(variant) && !unloadable.Contains(variant))
            return;
        var path = GetPath(variant);
        string? reason = unloadable.Contains(variant) ? "unloadable-before-overwrite" : null;
        if (reason is null)
        {
            try { ReadFile(path); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            {
                reason = "unreadable-before-overwrite";
            }
        }
        if (reason is not null && File.Exists(path))
        {
            var copy = Archive(path, variant, reason)
                ?? throw new IOException("Could not keep a copy of the saved look; it was not replaced.");
            StartupHook.Log($"Kept a copy of a Spektrafilm look that could not be loaded before replacing it: {copy}");
        }
        unloadable.Remove(variant);
        verified.Add(variant);
    }

    private (string Message, bool Protected) WriteRecovery(Guid variant, JsonObject look, Exception error)
    {
        try
        {
            Directory.CreateDirectory(recoveryDirectory);
            PanelFiles.WritePreset(GetRecoveryPath(variant), look);
            return ($"{error.Message} The latest edit is protected in {GetRecoveryPath(variant)} and will be restored next time.", true);
        }
        catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return ($"{error.Message} A recovery copy could not be written either ({recoveryError.Message}). Keep Capture One open until saving works again.", false);
        }
    }

    private string? Archive(string source, Guid variant, string reason)
    {
        if (!File.Exists(source))
            return null;
        var archives = Path.Combine(recoveryDirectory, "Preserved originals");
        Directory.CreateDirectory(archives);
        var destination = Path.Combine(archives, $"{variant:N}-{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string Serialize(VariantLook look) => JsonSerializer.Serialize(look);

    // The saved file is read again when it is replaced, so nothing is held in memory per variant.
    // A look that cannot be read or applied keeps nothing: it is archived instead (PreserveUnverified).
    private JsonObject ToJson(Guid variant, VariantLook look)
    {
        var json = JsonSerializer.SerializeToNode(look)!.AsObject();
        if (unloadable.Contains(variant))
            return json;
        var path = GetPath(variant);
        try
        {
            if (ReadFile(path) is not null && PanelFiles.ReadPreset<JsonNode>(path) is JsonObject saved)
                return UnknownLookFields.Keep(saved, json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or InvalidOperationException or ArgumentException) { }
        return json;
    }

    private static string CatalogKey(string database)
    {
        var normalized = Path.GetFullPath(database).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
