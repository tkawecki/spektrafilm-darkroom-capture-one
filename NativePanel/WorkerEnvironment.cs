using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

// Ported in spirit from nothing-complex/spektrafilm-for-capture-one 285f0aa (WorkerEnvironment, GPL-3.0).
// All creative settings travel over the renderer protocol. Inherited experiment variables such as
// SPEKTRAFILM_VULKAN_HALATION_PASS=0 or SPEKTRAFILM_VULKAN_BLUR_DOWNSAMPLE would otherwise silently
// disable or change whole film passes in the Viewer and in exports.
internal static class WorkerEnvironment
{
    // Variables that select hardware, isolate test caches or tune tiling without changing pixels.
    private static readonly HashSet<string> Retained = new(StringComparer.OrdinalIgnoreCase)
    {
        "SPEKTRAFILM_REFERENCE_CACHE",
        "SPEKTRAFILM_VULKAN_DEVICE_INDEX",
        "SPEKTRAFILM_VULKAN_TILE_WIDTH",
        "SPEKTRAFILM_VULKAN_TILE_HEIGHT",
    };

    internal static void Sanitize(ProcessStartInfo command)
    {
        foreach (var name in command.Environment.Keys.ToArray())
        {
            var remove = name.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("SPEKTRAFILM_", StringComparison.OrdinalIgnoreCase) && !Retained.Contains(name));
            if (remove)
                command.Environment.Remove(name);
        }
    }
}
