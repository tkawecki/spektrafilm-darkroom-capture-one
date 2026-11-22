using System;
using System.IO;

// Viewer settings are preferences of the person, not of a photo (upstream nothing-complex 285f0aa, HS-10,
// GPL-3.0, adapted). Storing them per variant made selecting an image switch the Viewer to native
// resolution or keep a stale drag size. Show source is session-only and never persisted.
// Defaults since 2026-11-21 (user request): Viewer resolution Auto and a 1600 px drag preview; a saved file keeps its values.
internal sealed record PanelPreferenceData(int Version = 1, string ViewerSize = "auto", int DragSize = 1600, bool DraftSynthesis = false,
    bool ConfirmRecipeExport = true);

internal static class PanelPreferences
{
    internal const string FileName = "Panel preferences.json";
    private static bool writable = true;
    private static PanelPreferenceData? current;

    /// <summary>
    /// Stored next to panel.log in the launcher's user-data folder. Without the launcher variable
    /// (tests, detached contexts) preferences stay in memory and nothing is written.
    /// </summary>
    internal static string? Pathname
    {
        get
        {
            var log = Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG");
            return string.IsNullOrWhiteSpace(log) ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(log))!, FileName);
        }
    }

    internal static bool Exists => Pathname is { } path && File.Exists(path);

    internal static PanelPreferenceData Current => current ??= Read();

    private static PanelPreferenceData Read()
    {
        try
        {
            if (Pathname is not { } path || !File.Exists(path)) return new();
            var preferences = PanelFiles.ReadPreset<PanelPreferenceData>(path);
            if (preferences.Version != 1) throw new InvalidDataException("Panel preferences are from a newer version.");
            return preferences;
        }
        catch (Exception error)
        {
            // Keep an unknown or damaged file untouched; looks are independent of it.
            writable = false;
            StartupHook.Log("Panel preferences could not be loaded: " + error.GetBaseException().Message);
            return new();
        }
    }

    internal static void Update(Func<PanelPreferenceData, PanelPreferenceData> edit)
    {
        var next = edit(Current);
        if (next == Current && Exists) return;
        current = next;
        if (!writable || Pathname is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PanelFiles.WritePreset(path, next);
        }
        catch (Exception error) { StartupHook.Log("Panel preferences could not be saved: " + error.GetBaseException().Message); }
    }
}
