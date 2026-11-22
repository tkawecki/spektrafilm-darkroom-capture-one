using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

internal enum ExportRgbSpace
{
    // Existing serialized numeric values and IDs remain unchanged.
    Srgb, AdobeRgb, ProPhotoRgb, LinearRec2020,
    LinearSrgb, LinearAdobeRgb, LinearProPhotoRgb, DisplayP3, LinearDisplayP3,
    DciP3, LinearDciP3, Rec2020, Aces2065, DavinciWideGamut,
    LinearDavinciWideGamut, VGamut, LinearVGamut,
}
internal readonly record struct ExportColorChoice(string Id, string Label, ExportRgbSpace Space);

internal static class ExportColorSpace
{
    internal readonly record struct Rgb(double Red, double Green, double Blue);
    internal static IReadOnlyList<ExportColorChoice> Choices { get; } = Array.AsReadOnly(new[] {
        new ExportColorChoice("srgb", "sRGB", ExportRgbSpace.Srgb),
        new("adobe_rgb", "Adobe RGB (1998)", ExportRgbSpace.AdobeRgb),
        new("prophoto", "ProPhoto RGB", ExportRgbSpace.ProPhotoRgb),
        new("linear_rec2020", "Linear Rec.2020", ExportRgbSpace.LinearRec2020),
        new("linear_srgb", "Linear sRGB", ExportRgbSpace.LinearSrgb),
        new("linear_adobe_rgb", "Linear Adobe RGB (1998)", ExportRgbSpace.LinearAdobeRgb),
        new("linear_prophoto", "Linear ProPhoto RGB", ExportRgbSpace.LinearProPhotoRgb),
        new("display_p3", "Display P3", ExportRgbSpace.DisplayP3),
        new("linear_display_p3", "Linear Display P3", ExportRgbSpace.LinearDisplayP3),
        new("dci_p3", "DCI-P3 (gamma 2.6)", ExportRgbSpace.DciP3),
        new("linear_dci_p3", "Linear DCI-P3", ExportRgbSpace.LinearDciP3),
        new("rec2020", "Rec.2020 (BT.2020 curve)", ExportRgbSpace.Rec2020),
        new("aces2065_1", "ACES2065-1 (linear AP0)", ExportRgbSpace.Aces2065),
        new("davinci_wide_gamut", "DaVinci Wide Gamut / Intermediate (log; no ICC)", ExportRgbSpace.DavinciWideGamut),
        new("linear_davinci_wide_gamut", "Linear DaVinci Wide Gamut", ExportRgbSpace.LinearDavinciWideGamut),
        new("v_gamut", "V-Gamut / V-Log (log; no ICC)", ExportRgbSpace.VGamut),
        new("linear_v_gamut", "Linear V-Gamut", ExportRgbSpace.LinearVGamut),
    });
    internal static string Describe(ExportRgbSpace space)
    { foreach (var item in Choices) if (item.Space == space) return item.Label; throw new ArgumentOutOfRangeException(nameof(space)); }
    internal static string GamutId(ExportRgbSpace space) => space switch
    {
        ExportRgbSpace.Srgb or ExportRgbSpace.LinearSrgb => "sRGB",
        ExportRgbSpace.AdobeRgb or ExportRgbSpace.LinearAdobeRgb => "Adobe RGB (1998)",
        ExportRgbSpace.ProPhotoRgb or ExportRgbSpace.LinearProPhotoRgb => "ProPhoto RGB",
        ExportRgbSpace.LinearRec2020 or ExportRgbSpace.Rec2020 => "ITU-R BT.2020",
        ExportRgbSpace.DisplayP3 or ExportRgbSpace.LinearDisplayP3 => "Display P3",
        ExportRgbSpace.DciP3 or ExportRgbSpace.LinearDciP3 => "DCI-P3",
        ExportRgbSpace.Aces2065 => "ACES2065-1",
        ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.LinearDavinciWideGamut => "DaVinci Wide Gamut",
        ExportRgbSpace.VGamut or ExportRgbSpace.LinearVGamut => "V-Gamut",
        _ => throw new ArgumentOutOfRangeException(nameof(space)),
    };
    internal static string TransferId(ExportRgbSpace space) => space switch
    {
        ExportRgbSpace.Srgb or ExportRgbSpace.DisplayP3 => "srgb",
        ExportRgbSpace.AdobeRgb => "adobe-rgb", ExportRgbSpace.ProPhotoRgb => "romm",
        ExportRgbSpace.DciP3 => "gamma2.6", ExportRgbSpace.Rec2020 => "bt2020",
        ExportRgbSpace.DavinciWideGamut => "davinci-intermediate", ExportRgbSpace.VGamut => "v-log",
        ExportRgbSpace.LinearSrgb or ExportRgbSpace.LinearAdobeRgb or ExportRgbSpace.LinearProPhotoRgb
            or ExportRgbSpace.LinearRec2020 or ExportRgbSpace.LinearDisplayP3 or ExportRgbSpace.LinearDciP3
            or ExportRgbSpace.Aces2065 or ExportRgbSpace.LinearDavinciWideGamut or ExportRgbSpace.LinearVGamut => "linear",
        _ => throw new ArgumentOutOfRangeException(nameof(space)),
    };
    internal static bool IsEncoded(ExportRgbSpace space) => TransferId(space) != "linear";
    internal static ExportRgbSpace Parse(string? id) => id switch
    {
        null or "" or "srgb" => ExportRgbSpace.Srgb,
        "adobe_rgb" => ExportRgbSpace.AdobeRgb,
        "prophoto" => ExportRgbSpace.ProPhotoRgb,
        "linear_rec2020" => ExportRgbSpace.LinearRec2020,
        "linear_srgb" => ExportRgbSpace.LinearSrgb,
        "linear_adobe_rgb" => ExportRgbSpace.LinearAdobeRgb,
        "linear_prophoto" => ExportRgbSpace.LinearProPhotoRgb,
        "display_p3" => ExportRgbSpace.DisplayP3,
        "linear_display_p3" => ExportRgbSpace.LinearDisplayP3,
        "dci_p3" => ExportRgbSpace.DciP3,
        "linear_dci_p3" => ExportRgbSpace.LinearDciP3,
        "rec2020" => ExportRgbSpace.Rec2020,
        "aces2065_1" => ExportRgbSpace.Aces2065,
        "davinci_wide_gamut" => ExportRgbSpace.DavinciWideGamut,
        "linear_davinci_wide_gamut" => ExportRgbSpace.LinearDavinciWideGamut,
        "v_gamut" => ExportRgbSpace.VGamut,
        "linear_v_gamut" => ExportRgbSpace.LinearVGamut,
        _ => throw new ArgumentException("Unknown export color space: " + id),
    };
    // Matrices derived from published chromaticities; double precision and no
    // gamut clipping, quantization, LUT lookup, black-point compensation or
    // perceptual gamut compression. Encode after the matrix; clamp only when
    // the caller packs its chosen destination integer format.
    private static readonly Rgb D65 = White(.3127, .3290);
    private static readonly Rgb D50 = new(.9642, 1, .8249); // Existing project ICC PCS white.
    private static readonly Matrix SrgbXyz = Primaries(.64, .33, .30, .60, .15, .06, D65);
    private static readonly Matrix AdobeXyz = Primaries(.64, .33, .21, .71, .15, .06, D65);
    private static readonly Matrix ProPhotoXyz = Primaries(.7347, .2653, .1596, .8404, .0366, .0001, D50);
    private static readonly Matrix Rec2020Xyz = Primaries(.708, .292, .170, .797, .131, .046, D65);
    private static readonly Matrix Bradford = new(.8951, .2664, -.1614, -.7502, 1.7135, .0367, .0389, -.0685, 1.0296);
    private static readonly Matrix AdaptD65D50 = Adapt(D65, D50);
    private static readonly Matrix SrgbAdobe = AdobeXyz.Inverse() * SrgbXyz;
    private static readonly Matrix SrgbProPhoto = ProPhotoXyz.Inverse() * AdaptD65D50 * SrgbXyz;
    private static readonly Matrix SrgbRec2020 = Rec2020Xyz.Inverse() * SrgbXyz;
    private static readonly Matrix AdobeSrgb = SrgbAdobe.Inverse(), ProPhotoSrgb = SrgbProPhoto.Inverse(), Rec2020Srgb = SrgbRec2020.Inverse();
    private static readonly Rgb DciWhite = White(.314, .351), D60 = White(.32168, .33767);
    private static readonly Matrix SrgbDisplayP3 = Primaries(.680, .320, .265, .690, .150, .060, D65).Inverse() * SrgbXyz;
    private static readonly Matrix SrgbDciP3 = Primaries(.680, .320, .265, .690, .150, .060, DciWhite).Inverse() * Adapt(D65, DciWhite) * SrgbXyz;
    private static readonly Matrix SrgbAces = Primaries(.7347, .2653, 0, 1, .0001, -.077, D60).Inverse() * Adapt(D65, D60) * SrgbXyz;
    private static readonly Matrix SrgbDavinci = Primaries(.8, .313, .1682, .9877, .079, -.1155, D65).Inverse() * SrgbXyz;
    private static readonly Matrix SrgbVGamut = Primaries(.730, .280, .165, .840, .100, -.030, D65).Inverse() * SrgbXyz;
    private static readonly Matrix DisplayP3Srgb = SrgbDisplayP3.Inverse(), DciP3Srgb = SrgbDciP3.Inverse(),
        AcesSrgb = SrgbAces.Inverse(), DavinciSrgb = SrgbDavinci.Inverse(), VGamutSrgb = SrgbVGamut.Inverse();

    internal static Rgb FromSrgb(double red, double green, double blue, ExportRgbSpace space)
    {
        var rgb = Checked(red, green, blue);
        // Preserve exact old sRGB values, including -0, before final packing.
        return space == ExportRgbSpace.Srgb ? rgb : FromLinearSrgb(DecodeSrgb(red), DecodeSrgb(green), DecodeSrgb(blue), space);
    }
    internal static Rgb ToSrgb(double red, double green, double blue, ExportRgbSpace space)
    {
        var rgb = Checked(red, green, blue);
        if (space == ExportRgbSpace.Srgb) return rgb;
        rgb = ToLinearSrgb(red, green, blue, space);
        return Checked(EncodeSrgb(rgb.Red), EncodeSrgb(rgb.Green), EncodeSrgb(rgb.Blue));
    }
    internal static Rgb FromLinearSrgb(double red, double green, double blue, ExportRgbSpace space)
    {
        var rgb = Checked(red, green, blue);
        rgb = space switch
        {
            ExportRgbSpace.Srgb or ExportRgbSpace.LinearSrgb => rgb,
            ExportRgbSpace.AdobeRgb or ExportRgbSpace.LinearAdobeRgb => SrgbAdobe * rgb,
            ExportRgbSpace.ProPhotoRgb or ExportRgbSpace.LinearProPhotoRgb => SrgbProPhoto * rgb,
            ExportRgbSpace.LinearRec2020 or ExportRgbSpace.Rec2020 => SrgbRec2020 * rgb,
            ExportRgbSpace.DisplayP3 or ExportRgbSpace.LinearDisplayP3 => SrgbDisplayP3 * rgb,
            ExportRgbSpace.DciP3 or ExportRgbSpace.LinearDciP3 => SrgbDciP3 * rgb,
            ExportRgbSpace.Aces2065 => SrgbAces * rgb,
            ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.LinearDavinciWideGamut => SrgbDavinci * rgb,
            ExportRgbSpace.VGamut or ExportRgbSpace.LinearVGamut => SrgbVGamut * rgb,
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };
        return Checked(Encode(rgb.Red, space), Encode(rgb.Green, space), Encode(rgb.Blue, space));
    }
    internal static Rgb ToLinearSrgb(double red, double green, double blue, ExportRgbSpace space)
    {
        Checked(red, green, blue);
        var rgb = new Rgb(Decode(red, space), Decode(green, space), Decode(blue, space));
        rgb = space switch
        {
            ExportRgbSpace.Srgb or ExportRgbSpace.LinearSrgb => rgb,
            ExportRgbSpace.AdobeRgb or ExportRgbSpace.LinearAdobeRgb => AdobeSrgb * rgb,
            ExportRgbSpace.ProPhotoRgb or ExportRgbSpace.LinearProPhotoRgb => ProPhotoSrgb * rgb,
            ExportRgbSpace.LinearRec2020 or ExportRgbSpace.Rec2020 => Rec2020Srgb * rgb,
            ExportRgbSpace.DisplayP3 or ExportRgbSpace.LinearDisplayP3 => DisplayP3Srgb * rgb,
            ExportRgbSpace.DciP3 or ExportRgbSpace.LinearDciP3 => DciP3Srgb * rgb,
            ExportRgbSpace.Aces2065 => AcesSrgb * rgb,
            ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.LinearDavinciWideGamut => DavinciSrgb * rgb,
            ExportRgbSpace.VGamut or ExportRgbSpace.LinearVGamut => VGamutSrgb * rgb,
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };
        return Checked(rgb.Red, rgb.Green, rgb.Blue);
    }
    // Deliberately the existing C#/Colour convention: all negative values stay
    // on the linear toe. Native shaders' signed-gamma sRGB is a different
    // extension; request linear_rec709 from either engine for wide export.
    internal static double EncodeSrgb(double value) => value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
    internal static double DecodeSrgb(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static double Encode(double value, ExportRgbSpace space) => space switch
    {
        ExportRgbSpace.Srgb or ExportRgbSpace.DisplayP3 => EncodeSrgb(value),
        ExportRgbSpace.AdobeRgb => Math.CopySign(Math.Pow(Math.Abs(value), 256d / 563), value),
        ExportRgbSpace.ProPhotoRgb => value < 1d / 512 ? 16 * value : Math.Pow(value, 1 / 1.8),
        ExportRgbSpace.DciP3 => Math.CopySign(Math.Pow(Math.Abs(value), 1 / 2.6), value),
        ExportRgbSpace.Rec2020 => value < .018 ? 4.5 * value : 1.099 * Math.Pow(value, .45) - .099,
        ExportRgbSpace.DavinciWideGamut => value <= .00262409 ? value * 10.44426855 : .07329248 * (Math.Log2(value + .0075) + 7),
        ExportRgbSpace.VGamut => value < .01 ? 5.6 * value + .125 : .241514 * Math.Log10(value + .00873) + .598206,
        ExportRgbSpace.LinearRec2020 or ExportRgbSpace.LinearSrgb or ExportRgbSpace.LinearAdobeRgb
            or ExportRgbSpace.LinearProPhotoRgb or ExportRgbSpace.LinearDisplayP3 or ExportRgbSpace.LinearDciP3
            or ExportRgbSpace.Aces2065 or ExportRgbSpace.LinearDavinciWideGamut or ExportRgbSpace.LinearVGamut => value,
        _ => throw new ArgumentOutOfRangeException(nameof(space)),
    };
    private static double Decode(double value, ExportRgbSpace space) => space switch
    {
        ExportRgbSpace.Srgb or ExportRgbSpace.DisplayP3 => DecodeSrgb(value),
        ExportRgbSpace.AdobeRgb => Math.CopySign(Math.Pow(Math.Abs(value), 563d / 256), value),
        ExportRgbSpace.ProPhotoRgb => value <= 1d / 32 ? value / 16 : Math.Pow(value, 1.8),
        ExportRgbSpace.DciP3 => Math.CopySign(Math.Pow(Math.Abs(value), 2.6), value),
        ExportRgbSpace.Rec2020 => value < 1.099 * Math.Pow(.018, .45) - .099 ? value / 4.5 : Math.Pow((value + .099) / 1.099, 1 / .45),
        ExportRgbSpace.DavinciWideGamut => value <= .02740668 ? value / 10.44426855 : Math.Pow(2, value / .07329248 - 7) - .0075,
        ExportRgbSpace.VGamut => value < .181 ? (value - .125) / 5.6 : Math.Pow(10, (value - .598206) / .241514) - .00873,
        ExportRgbSpace.LinearRec2020 or ExportRgbSpace.LinearSrgb or ExportRgbSpace.LinearAdobeRgb
            or ExportRgbSpace.LinearProPhotoRgb or ExportRgbSpace.LinearDisplayP3 or ExportRgbSpace.LinearDciP3
            or ExportRgbSpace.Aces2065 or ExportRgbSpace.LinearDavinciWideGamut or ExportRgbSpace.LinearVGamut => value,
        _ => throw new ArgumentOutOfRangeException(nameof(space)),
    };
    private static Rgb Checked(double r, double g, double b) => double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b)
        ? new(r, g, b) : throw new ArgumentException("Export color conversion requires finite RGB values.");

    // Log pairs preserve scene latitude in their encoded pixels. They have no
    // fabricated display ICC: reference also writes these pairs with metadata only.
    internal static string? ResolveProfileOrNull(ExportRgbSpace space, string? root = null, string? captureOneDirectory = null)
    {
        if ((uint)space < 4) return ResolveProfile(space, root, captureOneDirectory);
        if (space is ExportRgbSpace.DavinciWideGamut or ExportRgbSpace.VGamut) return null;
        string expectedHash = space switch {
            ExportRgbSpace.LinearSrgb => "398BE803A82E74653D5C53C96DFA00C1DA7D89B022B0C2AC3B801A74126907C2",
            ExportRgbSpace.LinearAdobeRgb => "1FEAD34E71113ADAA1B25E689F8EA8DBE0C1CDFB9134E69FC01EECDA97B6759C",
            ExportRgbSpace.LinearProPhotoRgb => "2AFC12296DBEC581BC3137A775A39AABADFA6153B004A1DDABAE1F68BB62375D",
            ExportRgbSpace.DisplayP3 => "1D16BB011EFF5E04F1EE721E621CD54863213FF0AA944BBF99182299D9A4DF29",
            ExportRgbSpace.LinearDisplayP3 => "7F1FAF6C0F9810A598D41C42B27F75836FA0EBF34374A55C0AE443E13D8D97EB",
            ExportRgbSpace.DciP3 => "6F4F360D31BCE4C427887F11DAFBAF948EB38B6C9B1F4B96264BF73A2EAA7988",
            ExportRgbSpace.LinearDciP3 => "17F27E2ABF21018BA346A7E84210A58811AAED9C891243519257A9F56273CC5C",
            ExportRgbSpace.Rec2020 => "EFACBB71CA64138E68CB9A238482DDC788D2452196A499CB6FE7DAA560994532",
            ExportRgbSpace.Aces2065 => "DCEAFC9B442878A18CB2D74C6E68CC8E1B8EA4E1374509E12F627B16038486CF",
            ExportRgbSpace.LinearDavinciWideGamut => "748360D8B30131B8A3A94FF9B7906D5085CAEB3842A8CEAAF6CFF1358105A81A",
            ExportRgbSpace.LinearVGamut => "57347E4B1C493C8C65AD282DE2ED0528546BC35A28A2A97178820AEA3F4E531A",
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };
        root ??= Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("The Spektrafilm runtime profile directory is unavailable.");
        string id = ""; foreach (var choice in Choices) if (choice.Space == space) { id = choice.Id; break; }
        string path = ValidateRgbProfile(Path.Combine(root, "profiles", "export", id + ".icc"));
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expectedHash)
            throw new InvalidDataException("The selected export ICC profile has changed; restore its matching profile before export: " + path);
        return path;
    }

    internal static string ResolveProfile(ExportRgbSpace space, string? root = null, string? captureOneDirectory = null)
    {
        if ((uint)space >= 4) return ResolveProfileOrNull(space, root, captureOneDirectory)
            ?? throw new InvalidOperationException("This log export pair uses gamut/transfer metadata without an ICC profile: " + Describe(space));
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
        string path;
        string? expectedHash = null;
        switch (space)
        {
            case ExportRgbSpace.Srgb: path = Path.Combine(system, "sRGB Color Space Profile.icm"); break;
            case ExportRgbSpace.AdobeRgb:
                captureOneDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Capture One", "Capture One");
                path = Path.Combine(captureOneDirectory, "Color Profiles", "Common", "Adobe RGB (1998).icm");
                break;
            case ExportRgbSpace.ProPhotoRgb:
            case ExportRgbSpace.LinearRec2020:
                root ??= Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
                if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("The Spektrafilm runtime profile directory is unavailable.");
                path = Path.Combine(root, "profiles", "preview", space == ExportRgbSpace.ProPhotoRgb ? "ProPhoto RGB.icc" : "Linear Rec.2020.icc");
                expectedHash = space == ExportRgbSpace.ProPhotoRgb ? "52FD9D9C4CB84C88E69EA0EBAF39C1BAAB871A4BD247010EE3AF6613768D0F9B"
                    : "A831F01D55F7D34983C94A5DB8A967FFC2FC64D33728ABA32F7906EDD4202931";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(space));
        }
        path = ValidateRgbProfile(path);
        if (expectedHash is not null && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expectedHash)
            throw new InvalidDataException("The selected project export ICC profile has changed; restore its matching profile before export: " + path);
        return path;
    }

    // Validate bounded structure and RGB/PCS metadata before WIC opens it. This
    // checks the embedded profile, not a monitor assignment or a destination
    // conversion: the caller must already have converted the pixel values.
    internal static string ValidateRgbProfile(string path)
    {
        path = Path.GetFullPath(path);
        using var input = File.OpenRead(path);
        if (input.Length is < 132 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid RGB ICC profile length.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        uint Read(int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        if (Read(0) != bytes.Length || !bytes.AsSpan(36, 4).SequenceEqual("acsp"u8)
            || !bytes.AsSpan(16, 4).SequenceEqual("RGB "u8)
            || !(bytes.AsSpan(20, 4).SequenceEqual("XYZ "u8) || bytes.AsSpan(20, 4).SequenceEqual("Lab "u8)))
            throw new InvalidDataException("Export requires a valid RGB ICC profile with XYZ or Lab PCS.");
        if (!(bytes.AsSpan(12, 4).SequenceEqual("mntr"u8) || bytes.AsSpan(12, 4).SequenceEqual("prtr"u8)
            || bytes.AsSpan(12, 4).SequenceEqual("scnr"u8) || bytes.AsSpan(12, 4).SequenceEqual("spac"u8)))
            throw new InvalidDataException("Device-link, abstract and named-color profiles cannot describe RGB export pixels.");
        uint count = Read(128);
        if (count == 0 || 132UL + count * 12UL > (ulong)bytes.Length) throw new InvalidDataException("Invalid ICC tag table.");
        for (int i = 0; i < count; ++i)
        {
            uint start = Read(132 + i * 12 + 4), length = Read(132 + i * 12 + 8);
            if (start < 132UL + count * 12UL || length < 8 || (ulong)start + length > (ulong)bytes.Length)
                throw new InvalidDataException("ICC tag extends outside its profile.");
        }
        return path;
    }

    private static Rgb White(double x, double y) => new(x / y, 1, (1 - x - y) / y);
    private static Matrix Primaries(double rx, double ry, double gx, double gy, double bx, double by, Rgb white)
    {
        var matrix = Matrix.Columns(White(rx, ry), White(gx, gy), White(bx, by));
        var scale = matrix.Inverse() * white;
        return matrix * Matrix.Diagonal(scale);
    }
    private static Matrix Adapt(Rgb from, Rgb to)
    {
        var source = Bradford * from; var destination = Bradford * to;
        return Bradford.Inverse() * Matrix.Diagonal(new(destination.Red / source.Red, destination.Green / source.Green, destination.Blue / source.Blue)) * Bradford;
    }
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F, double G, double H, double I)
    {
        internal static Matrix Columns(Rgb r, Rgb g, Rgb b) => new(r.Red, g.Red, b.Red, r.Green, g.Green, b.Green, r.Blue, g.Blue, b.Blue);
        internal static Matrix Diagonal(Rgb v) => new(v.Red, 0, 0, 0, v.Green, 0, 0, 0, v.Blue);
        public static Rgb operator *(Matrix m, Rgb v) => new(m.A * v.Red + m.B * v.Green + m.C * v.Blue, m.D * v.Red + m.E * v.Green + m.F * v.Blue, m.G * v.Red + m.H * v.Green + m.I * v.Blue);
        public static Matrix operator *(Matrix l, Matrix r) => Columns(l * new Rgb(r.A, r.D, r.G), l * new Rgb(r.B, r.E, r.H), l * new Rgb(r.C, r.F, r.I));
        internal Matrix Inverse()
        {
            double determinant = A * (E * I - F * H) - B * (D * I - F * G) + C * (D * H - E * G);
            return new((E * I - F * H) / determinant, (C * H - B * I) / determinant, (B * F - C * E) / determinant,
                (F * G - D * I) / determinant, (A * I - C * G) / determinant, (C * D - A * F) / determinant,
                (D * H - E * G) / determinant, (B * G - A * H) / determinant, (A * E - B * D) / determinant);
        }
    }
}
