using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed record PaperTextureAsset(string AssetId, string Name, int Width, int Height);
internal sealed class PaperTextureStore
{
    internal const long MaximumImportBytes = 64L * 1024 * 1024;
    private const int MaximumPayloadBytes = 16_000_000 * 3 + 16;
    private const long MaximumCacheBytes = 128L * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "SPTEX01\0"u8;
    internal string DirectoryPath { get; }
    private readonly object gate = new();
    private sealed record Cached(PaperTexture Texture, long Length, long ModifiedTicks);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private long cacheBytes;

    // directory is a persistent store for normalized textures. No source image
    // path is saved in a look; only the canonical content hash is stored.
    internal PaperTextureStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A texture store needs a persistent directory.");
        DirectoryPath = Path.GetFullPath(directory);
    }
    internal PaperTextureAsset Import(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > MaximumImportBytes) throw new InvalidDataException("Paper texture files must be at most 64 MiB.");
        // Read dimensions before CopyPixels triggers full decoding. Ignore an
        // embedded ICC just like the reference implementation's PIL convert('RGB'); never apply display ICC.
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Import a single-frame paper texture image.");
        var image = decoder.Frames[0]; int width = image.PixelWidth, height = image.PixelHeight;
        Dimensions(width, height);
        var converted = image.Format == PixelFormats.Rgb24 ? (BitmapSource)image : new FormatConvertedBitmap(image, PixelFormats.Rgb24, null, 0);
        int stride = checked(width * 3); var payload = new byte[checked(16 + stride * height)];
        Magic.CopyTo(payload); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), width); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), height);
        // Keep cancellation responsive during large codec reads; output remains
        // private until the entire normalized, hashed asset is complete.
        const int rows = 128; var block = new byte[checked(stride * Math.Min(rows, height))];
        for (int y = 0; y < height; y += rows)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(rows, height - y);
            converted.CopyPixels(new Int32Rect(0, y, width, count), block, stride, 0);
            block.AsSpan(0, count * stride).CopyTo(payload.AsSpan(16 + y * stride));
        }
        token.ThrowIfCancellationRequested(); string id = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        Directory.CreateDirectory(DirectoryPath); string destination = AssetPath(id);
        if (File.Exists(destination)) ValidateExisting(destination, id, token);
        else
        {
            string temporary = Path.Combine(DirectoryPath, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    for (int i = 0; i < payload.Length; i += 1024 * 1024)
                    { token.ThrowIfCancellationRequested(); output.Write(payload.AsSpan(i, Math.Min(1024 * 1024, payload.Length - i))); }
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { ValidateExisting(destination, id, token); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        string name = Path.GetFileName(path); if (name.Length > 160) name = name[..160];
        return new(id, name, width, height);
    }
    internal PaperTexture Load(string id, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); string path = AssetPath(id); var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The saved paper texture is missing from its immutable store: " + id, path);
        lock (gate)
        {
            if (cache.TryGetValue(id, out var known))
            {
                if (known.Length == info.Length && known.ModifiedTicks == info.LastWriteTimeUtc.Ticks) return known.Texture;
                cacheBytes -= known.Texture.Rgb.Length; cache.Remove(id);
            }
        }
        var payload = ValidateExisting(path, id, token);
        int width = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)), height = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12));
        var texture = new PaperTexture(width, height, payload.AsMemory(16));
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (cache.TryGetValue(id, out var added)) return added.Texture;
            while (order.Count > 0 && (cacheBytes + texture.Rgb.Length > MaximumCacheBytes || cache.Count >= 16 || order.Count >= 64))
                if (cache.Remove(order.Dequeue(), out var old)) cacheBytes -= old.Texture.Rgb.Length;
            cache.Add(id, new(texture, info.Length, info.LastWriteTimeUtc.Ticks)); order.Enqueue(id); cacheBytes += texture.Rgb.Length;
        }
        return texture;
    }
    private string AssetPath(string id)
    {
        if (!PaperTexture.IsAssetId(id)) throw new ArgumentException("Invalid paper texture asset ID.");
        return Path.Combine(DirectoryPath, id + ".sfptex");
    }
    private static byte[] ValidateExisting(string path, string id, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 19 or > MaximumPayloadBytes) throw new InvalidDataException("Invalid stored paper texture size.");
        var payload = new byte[(int)file.Length];
        for (int offset = 0; offset < payload.Length;)
        {
            token.ThrowIfCancellationRequested(); int read = file.Read(payload, offset, Math.Min(1024 * 1024, payload.Length - offset));
            if (read == 0) throw new EndOfStreamException("Stored paper texture ended unexpectedly."); offset += read;
        }
        if (!payload.AsSpan(0, 8).SequenceEqual(Magic) || Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() != id)
            throw new InvalidDataException("Stored paper texture content does not match its immutable ID: " + id);
        int width = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)), height = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(12));
        Dimensions(width, height);
        if ((long)width * height * 3 + 16 != payload.Length) throw new InvalidDataException("Stored paper texture dimensions do not match its payload.");
        return payload;
    }
    private static void Dimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16_000_000)
            throw new InvalidDataException("Paper textures support at most 16 MP and 8192 pixels per side.");
    }
}
