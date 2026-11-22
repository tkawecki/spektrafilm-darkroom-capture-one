using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed partial class SpatialPreview
{
    // Neutral geometry delegates to the historical writer, preserving its
    // exact float/packing paths, output dimensions, metadata and file bytes.
    private static (int PixelWidth, int PixelHeight, string Detail) WriteGeometryExportImage(string path,
        HostImageSource.Frame source, byte[]? pixels, double strength, PreviewInputSpace input, string encoding,
        System.Drawing.Rectangle crop, CancellationToken token, FinishingTransform finishing,
        ExportRgbSpace color, string? profile, int jpegQuality, ScanGeometrySettings geometry, bool intermediate = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsIdentity) return WriteExportImage(path, source, pixels, strength, input, encoding, crop,
            token, finishing, color, profile, jpegQuality, intermediate);
        var destination = ExportFileOptions.IsDng(path) ? ExportRgbSpace.LinearRec2020 : color;
        var finished = DecodeFinishedFloat(source, pixels, strength, input, encoding, crop, token, finishing, destination);
        var transformed = ScanGeometry.ApplyRgba(finished, crop.Width, crop.Height, geometry, token);
        if (ExportFileOptions.IsDng(path))
        {
            var info = SpektraExport.SaveLinearDng(path, transformed.Pixels, transformed.Width, transformed.Height, token);
            return (transformed.Width, transformed.Height, $" · scan geometry · linear Rec.2020 DNG; headroom {info.HeadroomStops} EV; {info.NegativeSamples} negative and {info.OverRangeSamples} over-range samples clipped");
        }
        var output = PackFinishedFloat(transformed.Pixels, transformed.Width, transformed.Height,
            ExportFileOptions.WorkingFormat(path), token);
        if (ExportFileOptions.IsJpeg(path)) SpektraExport.SaveJpegWithProfile(path, output, token, profile, jpegQuality);
        else SpektraExport.SaveWithProfile(path, output, token, profile, intermediate);
        return (transformed.Width, transformed.Height, " · scan geometry" +
            (ExportFileOptions.IsJpeg(path) ? $" · JPEG quality {jpegQuality}" : " · 16-bit"));
    }

    // Full-frame pointwise finishing/micro -> strength blend -> target colour
    // encoding -> C1 delivered crop. Geometry is deliberately a later caller
    // operation so its interpolation sees this unbounded target-space float.
    private static float[] DecodeFinishedFloat(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, System.Drawing.Rectangle crop, CancellationToken token,
        FinishingTransform? finishing, ExportRgbSpace destinationSpace)
    {
        token.ThrowIfCancellationRequested();
        if (crop.Left < 0 || crop.Top < 0 || crop.Width <= 0 || crop.Height <= 0
            || (long)crop.Left + crop.Width > frame.Width || (long)crop.Top + crop.Height > frame.Height)
            throw new InvalidDataException("Invalid crop for the finished image.");
        if ((long)frame.Width * frame.Height * 4 * sizeof(float) != frame.FloatBytes.Length
            || strength > 0 && (rendered is null || rendered.Length != frame.FloatBytes.Length))
            throw new InvalidDataException("The finished image needs a complete floating-point source and renderer frame.");
        if (!double.IsFinite(strength) || strength < 0 || strength > 1)
            throw new ArgumentOutOfRangeException(nameof(strength));
        var prepared = strength > 0 ? PrepareSpatialFinishing(frame, rendered, outputEncoding, finishing, token) : null;
        var transform = new SrgbTransform(space, prepared is null ? outputEncoding : "srgb", strength,
            prepared is null ? finishing : null, prepared is not null);
        bool directLinear = strength >= 1 && prepared is null && outputEncoding == "linear_rec709" && finishing?.IsIdentity != false;
        bool directSource = strength <= 0 && ((space, destinationSpace) is
            (PreviewInputSpace.Srgb, ExportRgbSpace.Srgb) or (PreviewInputSpace.AdobeRgb, ExportRgbSpace.AdobeRgb)
            or (PreviewInputSpace.ProPhotoRgb, ExportRgbSpace.ProPhotoRgb) or (PreviewInputSpace.LinearRec2020, ExportRgbSpace.LinearRec2020));
        var pixels = GC.AllocateUninitializedArray<float>(checked(crop.Width * crop.Height * 4));
        PreviewRows.Process(crop.Width, crop.Height, token, (first, last) =>
        {
            var source = MemoryMarshal.Cast<byte, float>(frame.FloatBytes.AsSpan());
            var floats = prepared is null ? MemoryMarshal.Cast<byte, float>(rendered.AsSpan()) : prepared.AsSpan();
            for (int y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                int destination = y * crop.Width * 4, i = ((crop.Top + y) * frame.Width + crop.Left) * 4;
                for (int x = 0; x < crop.Width; x++, i += 4)
                {
                    ExportColorSpace.Rgb rgb;
                    if (directSource) rgb = new(source[i], source[i + 1], source[i + 2]);
                    else if (directLinear) rgb = ExportColorSpace.FromLinearSrgb(floats[i], floats[i + 1], floats[i + 2], destinationSpace);
                    else
                    {
                        var encoded = transform.GetUnclippedSrgb(source, floats, i);
                        rgb = ExportColorSpace.FromSrgb(encoded.Red, encoded.Green, encoded.Blue, destinationSpace);
                    }
                    pixels[destination++] = (float)rgb.Red; pixels[destination++] = (float)rgb.Green;
                    pixels[destination++] = (float)rgb.Blue; pixels[destination++] = source[i + 3];
                }
            }
        });
        return pixels;
    }

    // Shared final packing for the geometry branch. Bgra32 is display-only;
    // TIFF/PNG/JPEG remain on RGB48/RGBA64 until their own container boundary.
    private static BitmapSource PackFinishedFloat(ReadOnlyMemory<float> pixels, int width, int height,
        PixelFormat format, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width < 1 || height < 1 || (long)width * height * 4 != pixels.Length)
            throw new InvalidDataException("Invalid finished pixel dimensions.");
        var source = pixels.Span;
        BitmapSource image;
        if (format == PixelFormats.Bgra32)
        {
            var packed = new byte[pixels.Length];
            for (int i = 0; i < source.Length; i += 4)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                packed[i] = (byte)Math.Round(Math.Clamp((double)source[i + 2], 0, 1) * 255);
                packed[i + 1] = (byte)Math.Round(Math.Clamp((double)source[i + 1], 0, 1) * 255);
                packed[i + 2] = (byte)Math.Round(Math.Clamp((double)source[i], 0, 1) * 255);
                packed[i + 3] = (byte)Math.Round(Math.Clamp((double)source[i + 3], 0, 1) * 255);
            }
            image = BitmapSource.Create(width, height, 96, 96, format, null, packed, checked(width * 4));
        }
        else
        {
            if (format != PixelFormats.Rgb48 && format != PixelFormats.Rgba64) throw new ArgumentException("Use BGRA32, RGB48 or RGBA64.");
            int channels = format == PixelFormats.Rgba64 ? 4 : 3;
            var packed = new ushort[checked(width * height * channels)];
            for (int p = 0; p < width * height; p++)
            {
                if ((p & 4095) == 0) token.ThrowIfCancellationRequested();
                for (int c = 0; c < channels; c++) packed[p * channels + c] = (ushort)Math.Round(Math.Clamp((double)source[p * 4 + c], 0, 1) * 65535);
            }
            image = BitmapSource.Create(width, height, 96, 96, format, null, packed, checked(width * channels * 2));
        }
        image.Freeze(); return image;
    }
}
