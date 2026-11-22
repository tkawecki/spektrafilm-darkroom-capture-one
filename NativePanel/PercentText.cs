using System.Globalization;

// Percentage fields in the curve and paper editors. They are shown in the Windows number format, and
// they accept both '.' and ',' as the decimal mark: a 0-100 percentage has no thousands separator, so
// either mark can only be the decimal point. A system set to a decimal comma refused "47.125" before.
internal static class PercentText
{
    internal static string Format(double fraction) => (fraction * 100).ToString("G17", CultureInfo.CurrentCulture);

    /// <summary>Parses a percentage between 0 and 100 (inclusive), written with either decimal mark.</summary>
    internal static bool TryParse(string text, out double percent)
    {
        var trimmed = text.Trim();
        var parsed = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out percent)
            || double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
        return parsed && double.IsFinite(percent) && percent >= 0 && percent <= 100;
    }
}
