using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

internal static class FinishingCurveStorage
{
    internal const int MaximumPoints = 64;
    internal static readonly ImmutableArray<FinishingPoint> Identity =
        ImmutableArray.Create(new FinishingPoint(0, 0), new FinishingPoint(1, 1));
    private const int MaximumCachedStrings = 256;
    private const int MaximumTextLength = 32768;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImmutableArray<FinishingPoint>> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    // Empty means no arbitrary override, so historical five-slider values remain
    // authoritative. Malformed saved data is reported, never silently discarded.
    internal static ImmutableArray<FinishingPoint> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        if (text.Length > MaximumTextLength) throw new FormatException("Curve data is too large.");
        lock (Gate) if (Cache.TryGetValue(text, out var known)) return known;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 || root.GetArrayLength() > MaximumPoints)
                throw new FormatException("A curve requires 2 to 64 points.");
            var points = ImmutableArray.CreateBuilder<FinishingPoint>(root.GetArrayLength());
            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2
                    || row[0].ValueKind != JsonValueKind.Number || row[1].ValueKind != JsonValueKind.Number
                    || !row[0].TryGetDouble(out double x) || !row[1].TryGetDouble(out double y))
                    throw new FormatException("Each curve point needs numeric input and output coordinates.");
                points.Add(new(x, y));
            }
            var normalized = Validate(points.MoveToImmutable());
            string canonical = Write(normalized);
            lock (Gate)
            {
                if (Cache.TryGetValue(canonical, out var existing)) normalized = existing;
                Add(canonical, normalized);
                Add(text, normalized);
            }
            return normalized;
        }
        catch (JsonException error) { throw new FormatException("Curve data is not a valid point list.", error); }
        catch (ArgumentException error) { throw new FormatException(error.Message, error); }
    }

    internal static string Serialize(ImmutableArray<FinishingPoint> points)
    {
        if (points.IsDefaultOrEmpty) return "";
        var normalized = Validate(points);
        string text = Write(normalized);
        lock (Gate) Add(text, normalized);
        return text;
    }

    internal static ImmutableArray<FinishingPoint> Validate(ImmutableArray<FinishingPoint> points)
    {
        if (points.IsDefaultOrEmpty || points.Length < 2 || points.Length > MaximumPoints)
            throw new ArgumentException("A curve requires 2 to 64 points.");
        foreach (var p in points)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)
                throw new ArgumentException("Curve coordinates must be finite and between 0 and 1.");
        bool sorted = true;
        for (int i = 1; i < points.Length; ++i) sorted &= points[i - 1].X < points[i].X;
        if (!sorted) points = points.OrderBy(p => p.X).ToImmutableArray();
        for (int i = 1; i < points.Length; ++i)
            if (points[i - 1].X == points[i].X)
                throw new ArgumentException("Each curve point must have a distinct input coordinate.");
        return points.Length == 2 && points[0] == Identity[0] && points[1] == Identity[1] ? Identity : points;
    }

    private static string Write(ImmutableArray<FinishingPoint> points)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var p in points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(p.X);
                writer.WriteNumberValue(p.Y);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Add(string text, ImmutableArray<FinishingPoint> points)
    {
        if (Cache.ContainsKey(text)) return;
        while (Cache.Count >= MaximumCachedStrings) Cache.Remove(Order.Dequeue());
        Cache.Add(text, points);
        Order.Enqueue(text);
    }
}
