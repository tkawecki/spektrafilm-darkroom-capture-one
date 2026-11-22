using System;
using System.IO;
using System.Text.Json;

internal static class PanelFiles
{
    // Older looks and versions can embed a 4 MiB imported preset (preset interop,
    // removed 2026-11-17). They must stay readable, so the limit is unchanged.
    private const int MaxPresetBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    internal static T ReadPreset<T>(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxPresetBytes)
            throw new InvalidDataException("The preset is larger than 8 MiB.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith("\uFEFF"u8))
            json = json[3..];
        return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("The preset contains no look settings.");
    }

    internal static void WritePreset<T>(string path, T preset)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preset, WriteOptions);
        if (bytes.Length > MaxPresetBytes)
            throw new InvalidDataException("The preset is larger than 8 MiB; the previous saved settings were retained.");
        WriteAtomically(path, stream => stream.Write(bytes));
    }

    internal static void WriteAtomically(string path, Action<Stream> write)
    {
        var destination = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, $".spektra-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
