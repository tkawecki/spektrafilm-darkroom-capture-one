// SPDX-License-Identifier: GPL-3.0-only
// Paper modes/order/nearest fit: The Dyeworks 2.2.3 paper_texture.py and
// controller_presets_canvas.py (Celian Le Bolloch; spektrafilm by Andrea Volpato).
// C# adaptation 2026-11-16: immutable texture IDs, bounded straight RGBA,
// cancellation, source-alpha preservation and consistent export padding.
// See THIRD-PARTY.md.
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

internal enum PaperBlendMode { Multiply, Screen, Overlay, SoftLight, LinearBurn, GrainMerge }
internal sealed record PaperTextureLayer(string AssetId, PaperBlendMode Mode = PaperBlendMode.Multiply,
    double Opacity = .35, string Name = "");
internal sealed record PaperDecorationSettings(bool Active = false, bool PaddingBurn = false,
    double PaddingFraction = .03, ImmutableArray<PaperTextureLayer> Layers = default)
{
    internal static readonly PaperDecorationSettings Default = new();
    internal bool IsIdentity => !Active || Layers.IsDefaultOrEmpty ||
        (!PaddingBurn || PaddingFraction == 0) && !HasVisibleLayer;
    private bool HasVisibleLayer { get { foreach (var layer in Layers) if (layer.Opacity > 0) return true; return false; } }
    internal void Validate()
    {
        if (!double.IsFinite(PaddingFraction) || PaddingFraction < 0 || PaddingFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(PaddingFraction), "Paper border fraction must be between 0 and 1.");
        if (Layers.IsDefaultOrEmpty) return;
        if (Layers.Length > 32) throw new ArgumentException("A paper finish supports up to 32 ordered texture layers.");
        foreach (var layer in Layers)
        {
            if (layer is null || !PaperTexture.IsAssetId(layer.AssetId)) throw new ArgumentException("A paper layer requires a valid immutable texture ID.");
            if (!Enum.IsDefined(layer.Mode) || !double.IsFinite(layer.Opacity) || layer.Opacity < 0 || layer.Opacity > 1)
                throw new ArgumentException("Invalid paper blend mode or opacity.");
            if (layer.Name is null || layer.Name.Length > 160 || layer.Name.Contains('\0')) throw new ArgumentException("Invalid paper texture label.");
        }
    }
}
internal sealed class PaperTexture
{
    private readonly byte[] pixels;
    internal int Width { get; }
    internal int Height { get; }
    internal ReadOnlyMemory<byte> Rgb => pixels;
    internal PaperTexture(int width, int height, ReadOnlyMemory<byte> rgb)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16_000_000
            || (long)width * height * 3 != rgb.Length) throw new ArgumentException("Paper textures require RGB8, up to 16 MP and 8192 pixels per side.");
        Width = width; Height = height; pixels = rgb.ToArray();
    }
    internal static bool IsAssetId(string? id)
    {
        if (id is null || id.Length != 64) return false;
        foreach (char c in id) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
internal readonly record struct PaperContentRect(int X, int Y, int Width, int Height);
internal sealed record PaperDecorationResult(ReadOnlyMemory<float> Pixels, int Width, int Height, PaperContentRect Content);
internal static class PaperDecoration
{
    internal static string ModeName(PaperBlendMode mode) => mode switch {
        PaperBlendMode.Multiply => "Multiply", PaperBlendMode.Screen => "Screen", PaperBlendMode.Overlay => "Overlay",
        PaperBlendMode.SoftLight => "Soft Light", PaperBlendMode.LinearBurn => "Linear Burn", PaperBlendMode.GrainMerge => "Grain Merge",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    // Upstream unknown mode fallback, useful only when importing DW state.
    internal static PaperBlendMode ParseDwMode(string? mode) => mode switch {
        "Screen" => PaperBlendMode.Screen, "Overlay" => PaperBlendMode.Overlay, "Soft Light" => PaperBlendMode.SoftLight,
        "Linear Burn" => PaperBlendMode.LinearBurn, "Grain Merge" => PaperBlendMode.GrainMerge, _ => PaperBlendMode.Multiply,
    };
    internal static PaperDecorationResult ApplyRgba(ReadOnlyMemory<float> pixels, int width, int height,
        PaperDecorationSettings settings, Func<string, PaperTexture> textureResolver, bool includePadding = true,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        Size(width, height);
        if ((long)width * height * 4 != pixels.Length) throw new ArgumentException("Paper decoration requires a complete RGBA image.");
        var content = new PaperContentRect(0, 0, width, height);
        if (settings.IsIdentity) return new(pixels, width, height, content);
        int padding = includePadding && settings.PaddingBurn ? (int)Math.Floor(Math.Max(width, height) * settings.PaddingFraction) : 0;
        int w = checked(width + 2 * padding), h = checked(height + 2 * padding); Size(w, h);
        var loaded = new (PaperTexture Texture, PaperTextureLayer Layer)[settings.Layers.Length]; int count = 0;
        foreach (var layer in settings.Layers)
        {
            token.ThrowIfCancellationRequested(); if (layer.Opacity == 0) continue;
            ArgumentNullException.ThrowIfNull(textureResolver);
            loaded[count++] = (textureResolver(layer.AssetId) ?? throw new ArgumentException("Paper texture could not be loaded: " + layer.AssetId), layer);
        }
        if (padding == 0 && count == 0) return new(pixels, width, height, content);
        var input = pixels.Span;
        for (int i = 0; i < input.Length; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            if (!float.IsFinite(input[i]) || (i & 3) == 3 && (input[i] < 0 || input[i] > 1))
                throw new ArgumentException("Paper decoration requires finite RGB and alpha coverage between 0 and 1.");
        }
        var output = new float[checked(w * h * 4)];
        if (padding > 0) Array.Fill(output, 1f); // target-space paper white, opaque coverage
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            input.Slice(y * width * 4, width * 4).CopyTo(output.AsSpan(((y + padding) * w + padding) * 4));
        }
        for (int layerIndex = 0; layerIndex < count; layerIndex++)
        {
            var (texture, layer) = loaded[layerIndex];
            var xs = Nearest(texture.Width, w); var ys = Nearest(texture.Height, h);
            float opacity = (float)layer.Opacity, inverse = (float)(1.0 - layer.Opacity);
            Parallel.For(0, h, new ParallelOptions { CancellationToken = token }, y =>
            {
                var tex = texture.Rgb.Span; int row = y * w * 4, textureRow = ys[y] * texture.Width * 3;
                for (int x = 0; x < w; x++)
                {
                    int at = row + x * 4, t = textureRow + xs[x] * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float b = output[at + c], value = tex[t + c] / 255f;
                        float mixed = Math.Clamp(Blend(b, value, layer.Mode), 0, 1);
                        output[at + c] = opacity >= 1 ? mixed : Math.Clamp(b * inverse + mixed * opacity, 0, 1);
                    }
                }
            });
        }
        return new(output, w, h, new(padding, padding, width, height));
    }
    private static int[] Nearest(int source, int target)
    {
        var result = new int[target]; if (target == 1) return result;
        double step = (source - 1) / (double)(target - 1);
        for (int i = 0; i < target - 1; i++) result[i] = (int)(i * step);
        result[target - 1] = source - 1; return result;
    }
    private static float Blend(float b, float t, PaperBlendMode mode) => mode switch {
        PaperBlendMode.Multiply => b * t,
        PaperBlendMode.Screen => 1f - (1f - b) * (1f - t),
        PaperBlendMode.Overlay => b <= .5f ? 2f * b * t : 1f - 2f * (1f - b) * (1f - t),
        PaperBlendMode.SoftLight => t <= .5f ? b - (1f - 2f * t) * b * (1f - b)
            : b + (2f * t - 1f) * ((b <= .25f ? ((16f * b - 12f) * b + 4f) * b : MathF.Sqrt(Math.Max(0, b))) - b),
        PaperBlendMode.LinearBurn => b + t - 1f,
        PaperBlendMode.GrainMerge => b + t - .5f,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static void Size(int width, int height)
    {
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 100_000_000)
            throw new ArgumentException("Paper decoration supports up to 100 MP and 16,384 pixels per side, including the border.");
    }
}
