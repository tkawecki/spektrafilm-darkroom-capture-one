using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class SpektraExport
{
    internal const string FileFilter = "16-bit TIFF image|*.tif|16-bit PNG image|*.png";

    internal static string ExtensionFor(string? format) => format?.ToUpperInvariant() switch
    {
        "PNG" => ".png",
        _ => ".tif",
    };

    internal static PixelFormat PixelFormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => PixelFormats.Rgb48,
        ".png" => PixelFormats.Rgba64,
        _ => throw new InvalidOperationException("Choose 16-bit TIFF or PNG for the Spektrafilm file."),
    };

    internal static void Save(string path, BitmapSource image, CancellationToken stop)
    {
        var profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        SaveWithProfile(path, image, stop, profilePath);
    }

    // Pixels must already be encoded in the selected RGB space. Merely
    // attaching an ICC profile does not convert them or recover sRGB clipping.
    // uncompressed: for an intermediate file that is read again at once (recipe export); ZIP otherwise.
    internal static void SaveWithProfile(string path, BitmapSource image, CancellationToken stop, string? profilePath, bool uncompressed = false)
    {
        stop.ThrowIfCancellationRequested();
        var format = PixelFormatFor(path);
        if (image.Format != format)
            throw new InvalidDataException($"Spektrafilm export requires unpremultiplied {format} pixels.");

        BitmapEncoder encoder = format == PixelFormats.Rgba64
            ? new PngBitmapEncoder()
            : new TiffBitmapEncoder { Compression = uncompressed ? TiffCompressOption.None : TiffCompressOption.Zip };
        var profile = profilePath is null ? null : new ReadOnlyCollection<ColorContext>(new[] {
            new ColorContext(new Uri(ExportColorSpace.ValidateRgbProfile(profilePath))) });
        encoder.Frames.Add(BitmapFrame.Create(image, null, null, profile));
        PanelFiles.WriteAtomically(path, stream => Encode(encoder, stream, stop));
    }

    // RGB48 is already encoded in profilePath's space. Quantize explicitly
    // once, here at the JPEG boundary; do not ask WIC to perform a colour
    // transform or silently discard an alpha channel.
    internal static void SaveJpegWithProfile(string path, BitmapSource image, CancellationToken stop,
        string? profilePath, int quality)
    {
        stop.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg"))
            throw new InvalidDataException("JPEG export requires a .jpg or .jpeg filename.");
        if (quality < 1 || quality > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), "JPEG quality must be between 1 and 100.");
        if (image.Format != PixelFormats.Rgb48)
            throw new InvalidDataException("JPEG export requires unpremultiplied RGB48 pixels without alpha.");
        if (profilePath is not null) profilePath = ExportColorSpace.ValidateRgbProfile(profilePath);
        int stride = checked(image.PixelWidth * 3);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        var row = new ushort[stride];
        for (int y = 0; y < image.PixelHeight; y++)
        {
            stop.ThrowIfCancellationRequested();
            image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, checked(stride * 2), 0);
            for (int x = 0; x < stride; x++)
                pixels[y * stride + x] = (byte)((row[x] + 128) / 257);
        }
        var rgb = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            PixelFormats.Rgb24, null, pixels, stride);
        rgb.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        var profile = profilePath is null ? null : new ReadOnlyCollection<ColorContext>(new[] { new ColorContext(new Uri(profilePath)) });
        encoder.Frames.Add(BitmapFrame.Create(rgb, null, null, profile));
        PanelFiles.WriteAtomically(path, stream => Encode(encoder, stream, stop));
    }

    // The encoder writes through a stream that stops at the next write once the export is cancelled (a 24 MP TIFF
    // took 2 to 3 seconds to stop otherwise). Ported from upstream ab4d569 (Spektrafilm for Capture One,
    // Luka Kauzlaric, GPL-3.0).
    private static void Encode(BitmapEncoder encoder, Stream stream, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        try
        {
            encoder.Save(stop.CanBeCanceled ? new CancelableEncoderStream(stream, stop) : stream);
        }
        catch (Exception error) when (stop.IsCancellationRequested && error is not OperationCanceledException)
        {
            // WIC can report the stream's cancellation as a codec error.
            throw new OperationCanceledException("Spektrafilm file encoding was cancelled.", error, stop);
        }
        stop.ThrowIfCancellationRequested();
    }

    private sealed class CancelableEncoderStream(Stream inner, CancellationToken stop) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
            stop.ThrowIfCancellationRequested();
            inner.Flush();
        }

        public override void SetLength(long value)
        {
            stop.ThrowIfCancellationRequested();
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            stop.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            stop.ThrowIfCancellationRequested();
            inner.Write(buffer);
        }
    }

    internal static LinearDngInfo SaveLinearDng(string path, ReadOnlyMemory<float> linearBt2020Rgba,
        int width, int height, CancellationToken stop) =>
        LinearDngWriter.Save(path, linearBt2020Rgba, width, height, stop);
}
