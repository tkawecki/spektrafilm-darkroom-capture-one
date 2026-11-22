using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

/// <summary>
/// Capture One 16.9 writes 16-bit PNG samples little-endian; the PNG standard requires big-endian, so other
/// programs read such a file as noise. The export compares the written file with its own render and, when the
/// samples are swapped, rewrites the image data in standard order. Every other chunk (profile, metadata) is kept.
/// Rows are streamed, so a 100 MP file needs a few rows of memory, not the whole image.
/// </summary>
internal static class RecipeExportPng
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private const int OutputChunk = 1 << 20;

    internal sealed record Image(int Width, int Height, int BitDepth, int Channels, bool Interlaced)
    {
        internal int PixelBytes => Math.Max(1, Channels * BitDepth / 8);
        internal long Stride => ((long)Width * Channels * BitDepth + 7) / 8;
    }

    private readonly record struct Chunk(string Type, long Start, long DataOffset, int Length);

    internal static Image ReadHeader(Stream png)
    {
        var head = new byte[33];
        png.Position = 0;
        png.ReadExactly(head);
        if (!head.AsSpan(0, 8).SequenceEqual(Signature) || BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8)) != 13
            || !head.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Not a PNG file.");
        var width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20));
        if (width <= 0 || height <= 0) throw new InvalidDataException("The PNG has no pixels.");
        var channels = head[25] switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Unknown PNG colour type.") };
        return new Image(width, height, head[24], channels, head[28] != 0);
    }

    /// <summary>
    /// True when the file reads back as the expected values only after swapping bytes. rows: (row index, expected
    /// 16-bit samples of that row); rows that cannot tell the two orders apart are ignored. The swapped reading
    /// must be clearly closer, so a converted or watermarked file is not swapped by mistake.
    /// </summary>
    internal static bool IsSwapped(Stream png, IReadOnlyDictionary<int, ushort[]> rows)
    {
        var image = ReadHeader(png);
        if (image.BitDepth != 16 || image.Interlaced) return false;
        double direct = 0, swapped = 0;
        var index = 0;
        foreach (var row in Rows(png, image))
        {
            if (rows.TryGetValue(index++, out var expected))
                for (var i = 0; i < Math.Min(expected.Length, row.Length / 2); i++)
                {
                    int big = row[2 * i] << 8 | row[2 * i + 1];
                    int little = row[2 * i + 1] << 8 | row[2 * i];
                    direct += Math.Abs(big - expected[i]);
                    swapped += Math.Abs(little - expected[i]);
                }
            if (index > rows.Keys.Max()) break;
        }
        return swapped * 2 < direct;
    }

    /// <summary>Writes a copy of a 16-bit PNG with its samples byte-swapped; all other chunks stay in place.</summary>
    internal static void SwapSamples(Stream source, Stream destination)
    {
        var image = ReadHeader(source);
        if (image.BitDepth != 16 || image.Interlaced) throw new InvalidDataException("Only non-interlaced 16-bit PNG samples can be swapped.");
        var chunks = Chunks(source).ToArray();
        destination.Write(Signature);
        var dataWritten = false;
        foreach (var chunk in chunks)
        {
            if (chunk.Type != "IDAT") { Copy(source, destination, chunk.Start, chunk.Length + 12L); continue; }
            if (dataWritten) continue;
            dataWritten = true;
            using var chunked = new ChunkWriter(destination);
            using (var deflater = new ZLibStream(chunked, CompressionLevel.Optimal, leaveOpen: true))
            {
                var previous = new byte[image.Stride];
                var filtered = new byte[image.Stride + 1];
                foreach (var row in Rows(source, image))
                {
                    for (var i = 0; i + 1 < row.Length; i += 2) (row[i], row[i + 1]) = (row[i + 1], row[i]);
                    Filter(row, previous, image.PixelBytes, filtered);
                    deflater.Write(filtered);
                    row.CopyTo(previous, 0);
                }
            }
        }
    }

    // Unfiltered rows, read through the IDAT chunks without holding the whole image.
    private static IEnumerable<byte[]> Rows(Stream png, Image image)
    {
        if (image.Interlaced) throw new InvalidDataException("Interlaced PNG is not supported.");
        var stride = checked((int)image.Stride);
        var data = new DataStream(png, Chunks(png).Where(c => c.Type == "IDAT").ToArray());
        using var inflater = new ZLibStream(data, CompressionMode.Decompress);
        var previous = new byte[stride];
        var raw = new byte[stride + 1];
        for (var y = 0; y < image.Height; y++)
        {
            inflater.ReadExactly(raw);
            var row = new byte[stride];
            Unfilter(raw[0], raw.AsSpan(1), previous, row, image.PixelBytes, y > 0);
            row.CopyTo(previous, 0);
            yield return row;
        }
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, ReadOnlySpan<byte> previous, Span<byte> row, int bpp, bool hasPrevious)
    {
        switch (filter)
        {
            case 0: source.CopyTo(row); break;
            case 1: for (var x = 0; x < row.Length; x++) row[x] = (byte)(source[x] + (x >= bpp ? row[x - bpp] : 0)); break;
            case 2: for (var x = 0; x < row.Length; x++) row[x] = (byte)(source[x] + (hasPrevious ? previous[x] : 0)); break;
            case 3: for (var x = 0; x < row.Length; x++) row[x] = (byte)(source[x] + ((x >= bpp ? row[x - bpp] : 0) + (hasPrevious ? previous[x] : 0)) / 2); break;
            case 4:
                for (var x = 0; x < row.Length; x++)
                    row[x] = (byte)(source[x] + Paeth(x >= bpp ? row[x - bpp] : 0, hasPrevious ? previous[x] : 0, hasPrevious && x >= bpp ? previous[x - bpp] : 0));
                break;
            default: throw new InvalidDataException("Unknown PNG row filter.");
        }
    }

    // The usual encoder heuristic: the filter with the smallest sum of absolute (signed) residuals.
    private static void Filter(byte[] row, byte[] previous, int bpp, byte[] output)
    {
        Span<byte> candidate = row.Length <= 4096 ? stackalloc byte[row.Length] : new byte[row.Length];
        long best = long.MaxValue;
        for (byte filter = 0; filter <= 4; filter++)
        {
            long sum = 0;
            for (var x = 0; x < row.Length; x++)
            {
                int left = x >= bpp ? row[x - bpp] : 0, up = previous[x], upLeft = x >= bpp ? previous[x - bpp] : 0;
                var value = (byte)(row[x] - filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, upLeft) });
                candidate[x] = value;
                sum += value < 128 ? value : 256 - value;
            }
            if (sum >= best) continue;
            best = sum;
            output[0] = filter;
            candidate.CopyTo(output.AsSpan(1));
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static IEnumerable<Chunk> Chunks(Stream png)
    {
        var header = new byte[8];
        for (long offset = 8; offset + 12 <= png.Length;)
        {
            png.Position = offset;
            png.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 0 || offset + 12L + length > png.Length) throw new InvalidDataException("Damaged PNG chunk.");
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            yield return new Chunk(type, offset, offset + 8, length);
            offset += 12L + length;
            if (type == "IEND") yield break;
        }
    }

    private static void Copy(Stream source, Stream destination, long start, long count)
    {
        source.Position = start;
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, read);
            count -= read;
        }
    }

    // The IDAT payloads of a PNG, read in order as one stream.
    private sealed class DataStream(Stream png, Chunk[] chunks) : Stream
    {
        private int chunk;
        private int used;
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (chunk < chunks.Length && used == chunks[chunk].Length) { chunk++; used = 0; }
            if (chunk == chunks.Length) return 0;
            png.Position = chunks[chunk].DataOffset + used;
            var read = png.Read(buffer, offset, Math.Min(count, chunks[chunk].Length - used));
            if (read == 0) throw new EndOfStreamException("The PNG image data ends early.");
            used += read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Compressed image data written as IDAT chunks of at most 1 MiB, each with its CRC.
    private sealed class ChunkWriter(Stream output) : Stream
    {
        private readonly MemoryStream pending = new();
        public override void Write(byte[] buffer, int offset, int count)
        {
            pending.Write(buffer, offset, count);
            if (pending.Length >= OutputChunk) Emit();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && pending.Length > 0) Emit();
            base.Dispose(disposing);
        }
        private void Emit()
        {
            var data = pending.ToArray();
            pending.SetLength(0);
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            output.Write(number);
            output.Write("IDAT"u8);
            output.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(number, Crc("IDAT"u8, data));
            output.Write(number);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
