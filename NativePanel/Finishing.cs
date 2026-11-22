// SPDX-License-Identifier: GPL-3.0-only
// Scan-finishing mathematics ported from The Dyeworks 2.2.3:
// spektrafilm/model/scan_finishing.py (SHA256
// 95e3e778f9e55e68f8ac25da1d4d67c0bbc6832cf72f5ee69081ded797f5d51e).
// Copyright (C) 2026 Andrea Volpato, original spektrafilm program.
// Copyright (C) 2026 Celian Le Bolloch (Natcelian), Dyeworks additions.
// Modified 2026-11-16: standalone immutable C# implementation for C1;
// encoded-sRGB wrapper, bounded parameter loading, no spatial operations.
// See the repository LICENSE for GPL-3.0 and tests/FinishingTests/README.md
// for provenance, reproduction, working-space and fidelity limits.
//
// The separation matrix below comes from NegPy; its permission notice:
// Copyright (c) 2023 NRG Systems, Inc.
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

internal readonly record struct FinishingPoint(double X, double Y);

/// <summary>Neutral defaults preserve historical pixels. Curves use immutable point arrays.</summary>
internal sealed record FinishingSettings
{
    public bool Active { get; init; } = true;
    public double ExposureEv { get; init; }
    public double GainRedEv { get; init; }
    public double GainGreenEv { get; init; }
    public double GainBlueEv { get; init; }
    public double ColorSeparation { get; init; }
    public double BlackPoint { get; init; }
    public double WhitePoint { get; init; } = 1;
    public double BlackRed { get; init; }
    public double BlackGreen { get; init; }
    public double BlackBlue { get; init; }
    public double WhiteRed { get; init; }
    public double WhiteGreen { get; init; }
    public double WhiteBlue { get; init; }
    public double Gamma { get; init; } = 1;
    public double Contrast { get; init; }
    public double MicroContrast { get; init; }
    public double Toe { get; init; }
    public double Shoulder { get; init; }
    public double Saturation { get; init; } = 1;
    public double Vibrance { get; init; } = 1;
    public double HueRed { get; init; } = 1;
    public double HueYellow { get; init; } = 1;
    public double HueGreen { get; init; } = 1;
    public double HueCyan { get; init; } = 1;
    public double HueBlue { get; init; } = 1;
    public double HueMagenta { get; init; } = 1;
    public double ShadowHue { get; init; } = 210;
    public double ShadowStrength { get; init; }
    public double MidtoneHue { get; init; } = 120;
    public double MidtoneStrength { get; init; }
    public double HighlightHue { get; init; } = 45;
    public double HighlightStrength { get; init; }
    public ImmutableArray<FinishingPoint> Curve { get; init; }
    public ImmutableArray<FinishingPoint> CurveRed { get; init; }
    public ImmutableArray<FinishingPoint> CurveGreen { get; init; }
    public ImmutableArray<FinishingPoint> CurveBlue { get; init; }

    internal static double Bound(double value, double neutral, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : neutral;

    // Fixed-x five-point controls fit existing persisted numeric dictionaries.
    // Direct API clients can supply arbitrary point arrays via Curve/CurveRed/etc.
    public static FinishingSettings FromDictionary(IReadOnlyDictionary<string, double> values)
    {
        double Get(string key, double neutral, double min, double max) =>
            values.TryGetValue("finishing" + key, out var v) ? Bound(v, neutral, min, max) : neutral;
        ImmutableArray<FinishingPoint> GetCurve(string channel)
        {
            string[] names = { "Black", "Shadows", "Midtones", "Highlights", "White" };
            var points = ImmutableArray.CreateBuilder<FinishingPoint>(5);
            bool neutral = true;
            for (int i = 0; i < 5; i++)
            {
                var x = i / 4d;
                var y = Get("Curve" + channel + names[i], x, 0, 1);
                points.Add(new(x, y));
                neutral &= x == y;
            }
            return neutral ? default : points.MoveToImmutable();
        }
        return new FinishingSettings
        {
            Active = Get("Active", 1, 0, 1) != 0,
            ExposureEv = Get("ExposureEv", 0, -4, 4),
            GainRedEv = Get("GainRedEv", 0, -2, 2), GainGreenEv = Get("GainGreenEv", 0, -2, 2), GainBlueEv = Get("GainBlueEv", 0, -2, 2),
            ColorSeparation = Get("ColorSeparation", 0, 0, 1),
            BlackPoint = Get("BlackPoint", 0, -.25, .5), WhitePoint = Get("WhitePoint", 1, .5, 1.5),
            BlackRed = Get("BlackRed", 0, -.25, .25), BlackGreen = Get("BlackGreen", 0, -.25, .25), BlackBlue = Get("BlackBlue", 0, -.25, .25),
            WhiteRed = Get("WhiteRed", 0, -.25, .25), WhiteGreen = Get("WhiteGreen", 0, -.25, .25), WhiteBlue = Get("WhiteBlue", 0, -.25, .25),
            Gamma = Get("Gamma", 1, .2, 3), Contrast = Get("Contrast", 0, -1, 1),
            MicroContrast = Get("MicroContrast", 0, -1, 1),
            Toe = Get("Toe", 0, 0, 1), Shoulder = Get("Shoulder", 0, 0, 1),
            Saturation = Get("Saturation", 1, 0, 2), Vibrance = Get("Vibrance", 1, 0, 2),
            HueRed = Get("HueRed", 1, 0, 2), HueYellow = Get("HueYellow", 1, 0, 2), HueGreen = Get("HueGreen", 1, 0, 2),
            HueCyan = Get("HueCyan", 1, 0, 2), HueBlue = Get("HueBlue", 1, 0, 2), HueMagenta = Get("HueMagenta", 1, 0, 2),
            ShadowHue = Get("ShadowHue", 210, 0, 360), ShadowStrength = Get("ShadowStrength", 0, 0, 1),
            MidtoneHue = Get("MidtoneHue", 120, 0, 360), MidtoneStrength = Get("MidtoneStrength", 0, 0, 1),
            HighlightHue = Get("HighlightHue", 45, 0, 360), HighlightStrength = Get("HighlightStrength", 0, 0, 1),
            Curve = GetCurve(""), CurveRed = GetCurve("Red"), CurveGreen = GetCurve("Green"), CurveBlue = GetCurve("Blue"),
        };
    }
}

/// <summary>
/// Thread-safe scalar scan finishing. Input/output: ENCODED sRGB, with an
/// sRGB decode/encode only when analog exposure/gains/separation is active.
/// No final gamut clipping: the caller quantizes/clips once. Active point curves
/// deliberately clamp to [0,1], matching upstream. Identity returns bit-for-bit.
/// </summary>
internal sealed class FinishingTransform
{
    private const int CurveSize = 1024;
    private readonly double gainR, gainG, gainB, separation;
    private readonly double blackR, blackG, blackB, spanR, spanG, spanB;
    private readonly double gamma, contrast, toe, shoulder, saturation, vibrance;
    private readonly double[] hueScales;
    private readonly (double R, double G, double B) shadow, midtone, highlight;
    private readonly float[]? curve;
    private readonly bool linear, levels, hue, tint;
    public bool IsIdentity { get; }
    public double MicroContrast { get; }

    public FinishingTransform(FinishingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        static double B(double v, double neutral, double min, double max) => FinishingSettings.Bound(v, neutral, min, max);
        var ev = B(settings.ExposureEv, 0, -4, 4);
        gainR = Math.Pow(2, ev + B(settings.GainRedEv, 0, -2, 2));
        gainG = Math.Pow(2, ev + B(settings.GainGreenEv, 0, -2, 2));
        gainB = Math.Pow(2, ev + B(settings.GainBlueEv, 0, -2, 2));
        separation = B(settings.ColorSeparation, 0, 0, 1);
        linear = gainR != 1 || gainG != 1 || gainB != 1 || separation != 0;
        var black = B(settings.BlackPoint, 0, -.25, .5);
        var white = B(settings.WhitePoint, 1, .5, 1.5);
        blackR = black + B(settings.BlackRed, 0, -.25, .25);
        blackG = black + B(settings.BlackGreen, 0, -.25, .25);
        blackB = black + B(settings.BlackBlue, 0, -.25, .25);
        var whiteR = white + B(settings.WhiteRed, 0, -.25, .25);
        var whiteG = white + B(settings.WhiteGreen, 0, -.25, .25);
        var whiteB = white + B(settings.WhiteBlue, 0, -.25, .25);
        spanR = Math.Max(whiteR - blackR, .001);
        spanG = Math.Max(whiteG - blackG, .001);
        spanB = Math.Max(whiteB - blackB, .001);
        levels = blackR != 0 || blackG != 0 || blackB != 0 || whiteR != 1 || whiteG != 1 || whiteB != 1;
        gamma = B(settings.Gamma, 1, .2, 3);
        contrast = B(settings.Contrast, 0, -1, 1);
        toe = .25 * B(settings.Toe, 0, 0, 1);
        shoulder = .25 * B(settings.Shoulder, 0, 0, 1);
        saturation = B(settings.Saturation, 1, 0, 2);
        vibrance = B(settings.Vibrance, 1, 0, 2);
        hueScales = new[] { B(settings.HueRed, 1, 0, 2), B(settings.HueYellow, 1, 0, 2), B(settings.HueGreen, 1, 0, 2),
            B(settings.HueCyan, 1, 0, 2), B(settings.HueBlue, 1, 0, 2), B(settings.HueMagenta, 1, 0, 2) };
        hue = hueScales.Any(x => x != 1);
        shadow = Tint(B(settings.ShadowHue, 210, 0, 360), B(settings.ShadowStrength, 0, 0, 1));
        midtone = Tint(B(settings.MidtoneHue, 120, 0, 360), B(settings.MidtoneStrength, 0, 0, 1));
        highlight = Tint(B(settings.HighlightHue, 45, 0, 360), B(settings.HighlightStrength, 0, 0, 1));
        tint = shadow != default || midtone != default || highlight != default;
        curve = BuildCurve(settings);
        MicroContrast = settings.Active ? B(settings.MicroContrast, 0, -1, 1) : 0;
        IsIdentity = !settings.Active || (!linear && !levels && gamma == 1 && contrast == 0 && toe == 0 && shoulder == 0 &&
            saturation == 1 && vibrance == 1 && !hue && !tint && curve is null && MicroContrast == 0);
    }

    public void Apply(ref double red, ref double green, ref double blue)
    {
        if (IsIdentity) return;
        double r = red, g = green, b = blue;
        if (linear)
        {
            r = Decode(r) * gainR; g = Decode(g) * gainG; b = Decode(b) * gainB;
            if (separation > 0)
            {
                var dr = -Math.Log10(Math.Max(r, 1e-6));
                var dg = -Math.Log10(Math.Max(g, 1e-6));
                var db = -Math.Log10(Math.Max(b, 1e-6));
                if (r > 1e-6) r = Math.Pow(10, -(dr - .05 * separation * dg - .02 * separation * db) / (1 - .07 * separation));
                if (g > 1e-6) g = Math.Pow(10, -(-.04 * separation * dr + dg - .08 * separation * db) / (1 - .12 * separation));
                if (b > 1e-6) b = Math.Pow(10, -(-.01 * separation * dr - .10 * separation * dg + db) / (1 - .11 * separation));
            }
            r = Encode(r); g = Encode(g); b = Encode(b);
        }
        if (levels) { r = (r - blackR) / spanR; g = (g - blackG) / spanG; b = (b - blackB) / spanB; }
        r = Tone(r); g = Tone(g); b = Tone(b);
        var luma = Luma(r, g, b);
        if (saturation != 1) { Scale(ref r, ref g, ref b, luma, saturation); luma = Luma(r, g, b); }
        if (vibrance != 1)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var pixelSat = Math.Clamp((max - min) / Math.Max(max, 1e-6), 0, 1);
            Scale(ref r, ref g, ref b, luma, 1 + (vibrance - 1) * (1 - pixelSat));
            luma = Luma(r, g, b);
        }
        if (hue)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var chroma = max - Math.Min(r, Math.Min(g, b));
            if (chroma > 1e-9)
            {
                var h = max == r ? Mod(60 * ((g - b) / chroma), 360) :
                    max == g ? 60 * ((b - r) / chroma) + 120 : 60 * ((r - g) / chroma) + 240;
                double weights = 0, scales = 0;
                for (int i = 0; i < 6; i++)
                {
                    var distance = Math.Abs(Mod(h - i * 60 + 180, 360) - 180);
                    var w = Math.Clamp(1 - distance / 60, 0, 1);
                    w = w * w * (3 - 2 * w);
                    weights += w; scales += w * hueScales[i];
                }
                if (weights > 1e-9) Scale(ref r, ref g, ref b, luma, scales / weights);
                luma = Luma(r, g, b);
            }
        }
        if (tint)
        {
            var l = Math.Clamp(luma, 0, 1);
            var sm = (1 - l) * (1 - l); var mm = 4 * l * (1 - l); var hm = l * l;
            r = r + sm * shadow.R + mm * midtone.R + hm * highlight.R;
            g = g + sm * shadow.G + mm * midtone.G + hm * highlight.G;
            b = b + sm * shadow.B + mm * midtone.B + hm * highlight.B;
        }
        if (curve is not null) { r = SampleCurve(r, 0); g = SampleCurve(g, 1); b = SampleCurve(b, 2); }
        // Numerical overflow protection only, not display gamut clipping.
        // Real image float values and supported controls are far below this boundary.
        red = Finite(r); green = Finite(g); blue = Finite(b);
    }

    private double Tone(double v)
    {
        if (gamma != 1 && v > 0) v = Math.Pow(v, 1 / gamma);
        if (contrast != 0) v = .5 + (v - .5) * (1 + contrast);
        if (toe > 0 && v < toe) v = toe * Math.Exp((v - toe) / toe);
        var knee = 1 - shoulder;
        if (shoulder > 0 && v > knee) v = 1 - shoulder * Math.Exp(-(v - knee) / shoulder);
        return v;
    }
    private static double Decode(double v) => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
    private static double Encode(double v) => v <= .0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - .055;
    private static double Luma(double r, double g, double b) => r * .2126 + g * .7152 + b * .0722;
    private static double Mod(double value, double divisor) => (value % divisor + divisor) % divisor;
    private static double Finite(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, -double.MaxValue, double.MaxValue);
    private static void Scale(ref double r, ref double g, ref double b, double luma, double scale)
    { r = luma + (r - luma) * scale; g = luma + (g - luma) * scale; b = luma + (b - luma) * scale; }
    private static (double R, double G, double B) Tint(double hue, double strength)
    {
        if (strength == 0) return default;
        var h = Mod(hue, 360); var x = 1 - Math.Abs(Mod(h / 60, 2) - 1);
        (double r, double g, double b) = ((int)(h / 60) % 6) switch
        { 0 => (1d, x, 0d), 1 => (x, 1d, 0d), 2 => (0d, 1d, x), 3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x) };
        var mean = (r + g + b) / 3;
        return (.25 * strength * (r - mean), .25 * strength * (g - mean), .25 * strength * (b - mean));
    }

    private double SampleCurve(double value, int channel)
    {
        // Handle pathological nonfinite arithmetic before integer conversion.
        var t = Math.Clamp(Finite(value), 0, 1) * (CurveSize - 1);
        var i = (int)t; var j = Math.Min(i + 1, CurveSize - 1);
        double a = curve![i * 3 + channel], b = curve[j * 3 + channel];
        return a + (b - a) * (t - i);
    }
    private static float[]? BuildCurve(FinishingSettings p)
    {
        var master = CurveEvaluator.Create(p.Curve);
        var red = CurveEvaluator.Create(p.CurveRed);
        var green = CurveEvaluator.Create(p.CurveGreen);
        var blue = CurveEvaluator.Create(p.CurveBlue);
        if (master is null && red is null && green is null && blue is null) return null;
        var result = new float[CurveSize * 3];
        for (int i = 0; i < CurveSize; i++)
        {
            var grid = i / (double)(CurveSize - 1);
            var value = master?.Evaluate(grid) ?? grid;
            result[3 * i] = (float)(red?.Evaluate(value) ?? value);
            result[3 * i + 1] = (float)(green?.Evaluate(value) ?? value);
            result[3 * i + 2] = (float)(blue?.Evaluate(value) ?? value);
        }
        return result;
    }
    private sealed class CurveEvaluator
    {
        private readonly FinishingPoint[] points;
        private readonly double[] tangents;
        private CurveEvaluator(FinishingPoint[] points)
        {
            this.points = points;
            tangents = new double[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var before = points[Math.Max(i - 1, 0)];
                var after = points[Math.Min(i + 1, points.Length - 1)];
                tangents[i] = (after.Y - before.Y) / (after.X - before.X);
            }
        }
        internal static CurveEvaluator? Create(ImmutableArray<FinishingPoint> source)
        {
            if (source.IsDefaultOrEmpty) return null;
            // Stable sorting plus last-point-wins matches upstream JSON normalization.
            var points = source.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
                .Select(p => new FinishingPoint(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)))
                .OrderBy(p => p.X).GroupBy(p => p.X).Select(g => g.Last()).ToArray();
            if (points.Length < 2 || (points.Length == 2 && points[0] == new FinishingPoint(0, 0) && points[1] == new FinishingPoint(1, 1))) return null;
            return new CurveEvaluator(points);
        }
        internal double Evaluate(double value)
        {
            value = Math.Clamp(value, points[0].X, points[^1].X);
            int i = 0;
            while (i < points.Length - 2 && value > points[i + 1].X) i++;
            var a = points[i]; var b = points[i + 1];
            var h = b.X - a.X; var t = (value - a.X) / h;
            var t2 = t * t; var t3 = t2 * t;
            var v = (2 * t3 - 3 * t2 + 1) * a.Y + (t3 - 2 * t2 + t) * h * tangents[i] +
                (-2 * t3 + 3 * t2) * b.Y + (t3 - t2) * h * tangents[i + 1];
            return Math.Clamp(Finite(v), 0, 1);
        }
    }
}
