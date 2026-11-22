using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

// Rolling the panel back to an older DLL must not erase settings a newer panel saved
// (nothing-complex/spektrafilm-for-capture-one 285f0aa lists this risk; upstream blocks such writes,
// this fork keeps the unknown members instead). The panel always writes every member it knows,
// so a member missing from the new JSON was written by another version and is copied over,
// except the few known keys below that this panel omits on purpose.
internal static class UnknownLookFields
{
    // Optics keys this panel knows but does not write: reset finishing curves (omitted on purpose), the
    // removed engine marker (keeping it would make a repaired look unloadable again) and the
    // old drag-preview switch, which only seeded the global Viewer preferences once.
    private static readonly HashSet<string> OmittedSelections = new(StringComparer.Ordinal)
    {
        "finishing-curve-master-points", "finishing-curve-red-points",
        "finishing-curve-green-points", "finishing-curve-blue-points", "processing-engine",
    };
    private static readonly HashSet<string> OmittedSwitches = new(StringComparer.Ordinal) { "quick-drag-preview" };

    /// <summary>Returns a copy of <paramref name="current"/> with the members of
    /// <paramref name="saved"/> that it lacks. Known values in <paramref name="current"/> always win.</summary>
    internal static JsonObject Keep(JsonObject saved, JsonObject current)
    {
        var result = (JsonObject)current.DeepClone();
        Merge(saved, result, "");
        return result;
    }

    private static void Merge(JsonObject saved, JsonObject target, string path)
    {
        foreach (var (name, value) in saved)
        {
            if (target.TryGetPropertyValue(name, out var existing))
            {
                if (value is JsonObject savedChild && existing is JsonObject targetChild)
                    Merge(savedChild, targetChild, path + "/" + name);
                continue;
            }
            if ((path == "/Optics/Selections" && OmittedSelections.Contains(name))
                || (path == "/Optics/Switches" && OmittedSwitches.Contains(name)))
                continue;
            target[name] = value?.DeepClone();
        }
    }
}
