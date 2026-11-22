using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal sealed record LookVersion(Guid Variant, DateTimeOffset Created, string Name, VariantLook Settings, int SchemaVersion = 1)
{
    public string Label => $"{Created.LocalDateTime:g} · {Name}";
}

internal static class LookVersions
{
    internal static LookVersion Save(string folder, Guid variant, string name, VariantLook settings)
    {
        if (variant == Guid.Empty) throw new ArgumentException("A version needs a variant.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120) throw new ArgumentException("Use a version name of 1–120 characters.");
        var version = new LookVersion(variant, DateTimeOffset.UtcNow, name.Trim(), BatchExportSupport.Clone(settings));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, version.Created.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        PanelFiles.WritePreset(path, version);
        return version;
    }

    internal static IReadOnlyList<LookVersion> Read(string folder, Guid variant, Action<string>? warning = null)
    {
        if (!Directory.Exists(folder)) return Array.Empty<LookVersion>();
        var versions = new List<LookVersion>();
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var version = PanelFiles.ReadPreset<LookVersion>(file);
                if (version.SchemaVersion != 1 || version.Variant != variant || version.Settings is null)
                    throw new InvalidDataException("Invalid version identity.");
                versions.Add(version with { Settings = BatchExportSupport.Clone(version.Settings) });
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
            { warning?.Invoke(Path.GetFileName(file) + ": " + error.Message); }
        }
        return versions.OrderByDescending(x => x.Created).ToArray();
    }
}
