// Linear DNG encoding follows The Dyeworks 2.2.3, spektrafilm_gui/linear_dng.py
// (Celian Le Bolloch; spektrafilm by Andrea Volpato), GPL-3.0.
// C# implementation modified 2026-11-16: streamed TIFF writer, cancellation,
// finite-value validation and explicit clipping counts; no source-camera EXIF.
// See THIRD-PARTY.md.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

internal readonly record struct LinearDngInfo(int HeadroomStops, double Peak, long NegativeSamples, long OverRangeSamples);

internal static class LinearDngWriter
{
    // Colour 0.4.7 ITU-R BT.2020: XYZ -> linear RGB, calibration white D65.
    private static readonly double[] ColorMatrix = {
        1.716651187971268, -.355670783776392, -.253366281373660,
        -.666684351832489, 1.616481236634939, .015768545813911,
        .017639857445311, -.042770613257809, .942103121235474 };

    // Input is full-precision linear BT.2020 RGBA after finishing and crop.
    // Alpha is deliberately not represented by LinearRaw's three RGB samples.
    // The caller must keep the backing buffer unchanged until this returns.
    internal static LinearDngInfo Save(string path, ReadOnlyMemory<float> rgba, int width, int height, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(path), ".dng", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Linear DNG export requires a .dng filename.");
        if (width <= 0 || height <= 0 || rgba.Length % 4 != 0 || (long)width * height != rgba.Length / 4)
            throw new ArgumentException("Linear DNG requires a complete, nonempty RGBA frame.");
        long negative = 0, over = 0;
        double peak = double.NegativeInfinity;
        int sourceStride = checked(width * 4);
        var source = rgba.Span;
        for (int y = 0; y < height; y++)
        {
            stop.ThrowIfCancellationRequested();
            for (int x = y * sourceStride; x < (y + 1) * sourceStride; x += 4)
                for (int c = 0; c < 3; c++)
                {
                    float value = source[x + c];
                    if (!float.IsFinite(value))
                        throw new InvalidDataException("Linear DNG contains a nonfinite RGB sample.");
                    if (value < 0) negative++;
                    if (value > 16) over++;
                    peak = Math.Max(peak, value);
                }
        }
        int stops = 0;
        while (stops < 4 && peak > (1 << stops)) stops++;
        var info = new LinearDngInfo(stops, peak, negative, over);
        PanelFiles.WriteAtomically(path, stream => Write(stream, rgba, width, height, info, stop));
        return info;
    }

    private sealed record Tag(ushort Id, ushort Type, uint Count, byte[] Data);
    private static byte[] Shorts(params ushort[] values)
    {
        var data = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), values[i]);
        return data;
    }
    private static byte[] Longs(params uint[] values)
    {
        var data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), values[i]);
        return data;
    }
    private static byte[] Rationals(params double[] values)
    {
        var data = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 8), checked((int)Math.Round(values[i] * 1_000_000)));
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 8 + 4), 1_000_000);
        }
        return data;
    }
    private static Tag Text(ushort id, string value)
    {
        var data = Encoding.ASCII.GetBytes(value + "\0");
        return new Tag(id, 2, (uint)data.Length, data);
    }
    private static uint Align(uint value) => checked((value + 3) & ~3u);

    private static void Write(Stream stream, ReadOnlyMemory<float> rgba, int width, int height,
        LinearDngInfo info, CancellationToken stop)
    {
        uint stripBytes = checked((uint)((long)width * height * 6));
        var stripOffset = Longs(0);
        var tags = new List<Tag> {
            new(254, 4, 1, Longs(0)),                         // NewSubfileType: main image
            new(256, 4, 1, Longs((uint)width)),
            new(257, 4, 1, Longs((uint)height)),
            new(258, 3, 3, Shorts(16, 16, 16)),
            new(259, 3, 1, Shorts(1)),                       // uncompressed
            new(262, 3, 1, Shorts(34892)),                   // LinearRaw, never CFA
            new(273, 4, 1, stripOffset),
            new(274, 3, 1, Shorts(1)),
            new(277, 3, 1, Shorts(3)),
            new(278, 4, 1, Longs((uint)height)),
            new(279, 4, 1, Longs(stripBytes)),
            new(284, 3, 1, Shorts(1)),                       // contiguous RGB
            Text(305, "Spektrafilm C1"),
            new(339, 3, 3, Shorts(1, 1, 1)),                 // unsigned integer
            new(50706, 1, 4, new byte[] { 1, 4, 0, 0 }),
            new(50707, 1, 4, new byte[] { 1, 1, 0, 0 }),
            // Required DNG identifier describes this synthetic RGB space;
            // it does not impersonate the camera that supplied the source.
            Text(50708, "Spektrafilm C1 rendered RGB (linear BT.2020)"),
            new(50714, 3, 3, Shorts(0, 0, 0)),
            new(50717, 3, 3, Shorts(65535, 65535, 65535)),
            new(50719, 4, 2, Longs(0, 0)),
            new(50720, 4, 2, Longs((uint)width, (uint)height)),
            new(50721, 10, 9, Rationals(ColorMatrix)),
            new(50728, 5, 3, Rationals(1, 1, 1)),
            new(50730, 10, 1, Rationals(info.HeadroomStops)),
            new(50778, 3, 1, Shorts(21)),                    // D65
            new(50829, 4, 4, Longs(0, 0, (uint)height, (uint)width)),
        };
        tags.Sort((a, b) => a.Id.CompareTo(b.Id));
        uint extraOffset = Align(checked((uint)(8 + 2 + tags.Count * 12 + 4)));
        uint pixelOffset = extraOffset;
        foreach (var tag in tags) if (tag.Data.Length > 4) pixelOffset = Align(checked(pixelOffset + (uint)tag.Data.Length));
        _ = checked(pixelOffset + stripBytes); // Classic TIFF's 32-bit offset limit.
        BinaryPrimitives.WriteUInt32LittleEndian(stripOffset, pixelOffset);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u);
        writer.Write(checked((ushort)tags.Count));
        uint offset = extraOffset;
        foreach (var tag in tags)
        {
            writer.Write(tag.Id); writer.Write(tag.Type); writer.Write(tag.Count);
            if (tag.Data.Length <= 4)
            {
                writer.Write(tag.Data);
                for (int n = tag.Data.Length; n < 4; n++) writer.Write((byte)0);
            }
            else { writer.Write(offset); offset = Align(checked(offset + (uint)tag.Data.Length)); }
        }
        writer.Write(0u); // no next IFD
        while (stream.Position < extraOffset) writer.Write((byte)0);
        foreach (var tag in tags)
            if (tag.Data.Length > 4)
            {
                writer.Write(tag.Data);
                while ((stream.Position & 3) != 0) writer.Write((byte)0);
            }
        var row = new byte[checked(width * 6)];
        var source = rgba.Span;
        double scale = 65535.0 / (1 << info.HeadroomStops);
        for (int y = 0; y < height; y++)
        {
            stop.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                {
                    double value = Math.Clamp(source[(y * width + x) * 4 + c] * scale, 0, 65535);
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan((x * 3 + c) * 2), (ushort)Math.Round(value));
                }
            writer.Write(row);
        }
        stop.ThrowIfCancellationRequested();
    }
}
