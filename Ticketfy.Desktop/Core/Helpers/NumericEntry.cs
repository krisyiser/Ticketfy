using System.Globalization;

namespace Ticketfy.Core.Helpers;

/// <summary>Operator entry accepts a decimal dot or comma, never grouped numbers or exponents.</summary>
public static class NumericEntry
{
    private const NumberStyles Style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
    private static string Normalize(string? text) => (text ?? "").Trim().Replace(',', '.');
    public static bool TryDecimal(string? text, out decimal value) =>
        decimal.TryParse(Normalize(text), Style, CultureInfo.InvariantCulture, out value);
    public static bool TryQuantity(string? text, out double value) =>
        double.TryParse(Normalize(text), Style, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    public static bool IsMoney(decimal value) => value >= 0m && value <= long.MaxValue / 100m && decimal.Round(value, 2) == value;
}
