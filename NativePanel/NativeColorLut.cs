using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// A 3D LUT represents a pointwise RGB transform. Adaptive exposure and spatial
// optics must never be evaluated on the artificial color grid as if it were a photo.
internal static class NativeColorLut
{
    internal static byte[] CreateGrid(int size)
    {
        ValidateSize(size);
        var bytes = new byte[checked(size * size * size * 4 * sizeof(float))];
        var floats = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        var i = 0;
        for (var blue = 0; blue < size; blue++)
        for (var green = 0; green < size; green++)
        for (var red = 0; red < size; red++)
        {
            floats[i++] = (float)red / (size - 1);
            floats[i++] = (float)green / (size - 1);
            floats[i++] = (float)blue / (size - 1);
            floats[i++] = 1;
        }
        return bytes;
    }

    internal static string[] ColorOnlyOptions(string[] arguments)
    {
        if (arguments.Length % 2 != 0) throw new ArgumentException("Incomplete renderer options.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Length; i += 2) options[arguments[i]] = arguments[i + 1];
        foreach (var (key, value) in new (string, string)[]
        {
            ("input-color", "srgb"), ("output-color", "srgb"), ("auto-exposure", "false"),
            ("grain-model", "off"), ("halation-amount", "0"), ("scatter-amount", "0"),
            ("camera-diffusion-family", "off"), ("print-diffusion-family", "off"),
            ("dir-diffusion", "0"), ("dir-tail-weight", "0"),
            ("scanner-glare", "0"), ("scanner-mtf", "0"),
            ("scanner-unsharp-amount", "0"), ("grain-final-blur", "0"),
            ("enlarger-scale", "1"), ("enlarger-x", "0"), ("enlarger-y", "0"),
        }) options["--" + key] = value;
        var result = new List<string>(options.Count * 2);
        foreach (var (key, value) in options) { result.Add(key); result.Add(value); }
        return result.ToArray();
    }

    internal static string Encode(int size, double[] rgb)
    {
        ValidateSize(size);
        if (rgb.Length != size * size * size * 3) throw new ArgumentException("Invalid LUT node count.");
        var result = new StringBuilder(rgb.Length * 13);
        result.AppendLine("# Spektrafilm C1 color-only LUT; sRGB encoded input and output");
        result.AppendLine("# Film/profile models: Andrea Volpato; OFX adaptation: Aedan Diez.");
        result.AppendLine("# The Dyeworks additions: Celian Le Bolloch (Natcelian), version 2.2.3.");
        result.AppendLine("# Sources: https://github.com/andreavolpato/spektrafilm ; https://github.com/natcelian/spektrafilm.exe");
        result.AppendLine("# Bundled profile data: CC BY-SA 4.0 — https://creativecommons.org/licenses/by-sa/4.0/");
        result.AppendLine("# Imported profile rights remain with their original authors; retain their license and metadata.");
        result.AppendLine("# Modified: C1 selected parameters, pointwise finishing and native color-grid bake.");
        result.AppendLine("# Includes pointwise film/paper, finishing and look strength.");
        result.AppendLine("# Excludes adaptive exposure, spatial optics/grain, crop and C1 input development.");
        result.AppendLine("TITLE \"Spektrafilm C1 color-only sRGB\"");
        result.Append("LUT_3D_SIZE ").Append(size).Append('\n');
        result.AppendLine("DOMAIN_MIN 0.0 0.0 0.0");
        result.AppendLine("DOMAIN_MAX 1.0 1.0 1.0");
        for (var i = 0; i < rgb.Length; i++)
        {
            if (!double.IsFinite(rgb[i])) throw new InvalidDataException("The LUT contains an invalid color.");
            result.Append(Math.Clamp(rgb[i], 0, 1).ToString("0.##########", CultureInfo.InvariantCulture));
            result.Append(i % 3 == 2 ? '\n' : ' ');
        }
        return result.ToString();
    }

    private static void ValidateSize(int size)
    {
        if (size is not (33 or 64 or 65)) throw new ArgumentOutOfRangeException(nameof(size));
    }
}
