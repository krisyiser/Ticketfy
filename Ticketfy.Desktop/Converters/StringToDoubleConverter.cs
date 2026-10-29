using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ticketfy.Converters;

public class StringToDoubleConverter : IValueConverter
{
    public static readonly StringToDoubleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return d.ToString("G", culture);
        }
        if (value is decimal dec)
        {
            return dec.ToString("G", culture);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var numericType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        string text = (value as string ?? string.Empty).Trim();
        if (text.Length == 0)
            return Nullable.GetUnderlyingType(targetType) is not null ? null
                : numericType == typeof(decimal) ? (object)0m : 0d;
        // These are ungrouped entry fields. Never interpret a decimal separator as thousands.
        const NumberStyles style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        string normalized = text.Replace(',', '.');
        if (numericType == typeof(decimal) && decimal.TryParse(normalized, style, CultureInfo.InvariantCulture, out var money))
            return money;
        if (numericType == typeof(double) && double.TryParse(normalized, style, CultureInfo.InvariantCulture, out var quantity) && double.IsFinite(quantity))
            return quantity;
        throw new FormatException("Introduzca un número válido, sin separadores de miles.");
    }
}
