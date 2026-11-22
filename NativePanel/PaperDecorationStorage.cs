using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class PaperDecorationStorage
{
    internal const string SelectionKey = "paper-decoration";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PaperDecorationSettings> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    internal static PaperDecorationSettings FromSettings(IReadOnlyDictionary<string, string>? selections) =>
        Parse(selections is not null && selections.TryGetValue(SelectionKey, out var text) ? text : null);
    internal static void Capture(PaperDecorationSettings settings, IDictionary<string, string> selections)
    { ArgumentNullException.ThrowIfNull(selections); selections[SelectionKey] = Serialize(settings); }
    internal static PaperDecorationSettings Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PaperDecorationSettings.Default;
        if (text.Length > 32768) throw new FormatException("Paper decoration data is too large.");
        lock (Gate) if (Cache.TryGetValue(text, out var saved)) return saved;
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1)
                throw new FormatException("Unsupported paper decoration format.");
            var array = root.GetProperty("layers");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32) throw new FormatException("Invalid paper texture layer list.");
            var layers = ImmutableArray.CreateBuilder<PaperTextureLayer>(array.GetArrayLength());
            foreach (var layer in array.EnumerateArray())
            {
                string mode = layer.GetProperty("mode").GetString() ?? "";
                var parsed = PaperDecoration.ParseDwMode(mode);
                if (PaperDecoration.ModeName(parsed) != mode) throw new FormatException("Unknown saved paper blend mode: " + mode);
                layers.Add(new(layer.GetProperty("asset").GetString() ?? "", parsed, layer.GetProperty("opacity").GetDouble(),
                    layer.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""));
            }
            var result = new PaperDecorationSettings(root.GetProperty("active").GetBoolean(), root.GetProperty("paddingBurn").GetBoolean(),
                root.GetProperty("paddingFraction").GetDouble(), layers.MoveToImmutable());
            result.Validate(); string canonical = Write(result);
            lock (Gate)
            {
                if (Cache.TryGetValue(canonical, out var existing)) result = existing;
                Add(canonical, result); Add(text, result);
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        { throw new FormatException("Invalid saved paper decoration. Its original data was not changed.", error); }
    }
    internal static string Serialize(PaperDecorationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate(); string text = Write(settings);
        lock (Gate) Add(text, settings); return text;
    }
    private static string Write(PaperDecorationSettings settings)
    {
        using var memory = new MemoryStream();
        using (var json = new Utf8JsonWriter(memory))
        {
            json.WriteStartObject(); json.WriteNumber("version", 1); json.WriteBoolean("active", settings.Active);
            json.WriteBoolean("paddingBurn", settings.PaddingBurn); json.WriteNumber("paddingFraction", settings.PaddingFraction);
            json.WriteStartArray("layers");
            if (!settings.Layers.IsDefaultOrEmpty) foreach (var layer in settings.Layers)
            {
                json.WriteStartObject(); json.WriteString("asset", layer.AssetId); json.WriteString("name", layer.Name);
                json.WriteString("mode", PaperDecoration.ModeName(layer.Mode)); json.WriteNumber("opacity", layer.Opacity); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
    private static void Add(string text, PaperDecorationSettings settings)
    {
        if (Cache.ContainsKey(text)) return;
        while (Order.Count >= 256) Cache.Remove(Order.Dequeue());
        Cache.Add(text, settings); Order.Enqueue(text);
    }
}
