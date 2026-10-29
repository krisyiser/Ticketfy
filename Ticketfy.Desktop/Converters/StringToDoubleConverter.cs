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
        if (numericType == typeof(decimal) && Ticketfy.Core.Helpers.NumericEntry.TryDecimal(text, out var money))
            return money;
        if (numericType == typeof(double) && Ticketfy.Core.Helpers.NumericEntry.TryQuantity(text, out var quantity))
            return quantity;
        throw new FormatException("Introduzca un número válido, sin separadores de miles.");
    }
}
