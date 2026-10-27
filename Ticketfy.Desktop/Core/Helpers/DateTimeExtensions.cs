using System.Globalization;
using System.Text.RegularExpressions;

namespace Ticketfy.Core.Helpers;

/// <summary>Business calendar independent of the Windows display zone. Persist instants in UTC.</summary>
public static partial class BusinessTime
{
    public const string DefaultZoneId = "America/Mexico_City";
    private static TimeZoneInfo _zone = Resolve(DefaultZoneId);
    public static TimeZoneInfo Zone => _zone;
    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime;
    public static DateTime Today => Now.Date;
    public static TimeZoneInfo Resolve(string zoneId) => TimeZoneInfo.FindSystemTimeZoneById(zoneId);
    // Applied once at application bootstrap. Settings changes require a restart.
    public static void Configure(string zoneId) => _zone = Resolve(zoneId);

    [GeneratedRegex(@"(?:[zZ]|[+-]\d{2}:?\d{2})$")]
    private static partial Regex ExplicitOffset();

    public static bool TryLocal(string? value, out DateTime local, TimeZoneInfo? zone = null)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        zone ??= Zone;
        if (ExplicitOffset().IsMatch(value.Trim()) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var instant))
        {
            local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
            return true;
        }
        // Legacy timestamps without an offset are business wall-clock values, not OS-local instants.
        // Mexican legacy exports use day/month/year; never guess using the host's locale.
        var culture = value.Contains('/') ? CultureInfo.GetCultureInfo("es-MX") : CultureInfo.InvariantCulture;
        if (DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var legacy))
        {
            local = DateTime.SpecifyKind(legacy, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }

    public static DateTime ToUtc(DateTime wallClock, TimeZoneInfo? zone = null)
    {
        if (wallClock.Kind == DateTimeKind.Utc) return wallClock;
        zone ??= Zone;
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified) || zone.IsAmbiguousTime(unspecified))
            throw new InvalidOperationException("Hora comercial inexistente o ambigua por cambio horario; indique un instante con desplazamiento explícito.");
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    public static bool TryInstant(string? value, out DateTimeOffset instant, TimeZoneInfo? zone = null)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (ExplicitOffset().IsMatch(value.Trim()))
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out instant);
        zone ??= Zone;
        if (!TryLocal(value, out var local, zone) || zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) return false;
        instant = new DateTimeOffset(ToUtc(local, zone));
        if (TimeZoneInfo.ConvertTime(instant, zone).DateTime != local) { instant = default; return false; }
        return true;
    }
}

public static class DateTimeExtensions
{
    public static string ToLocalDisplayString(string? dateString, string format = "g") =>
        BusinessTime.TryLocal(dateString, out var local) ? local.ToString(format) : dateString ?? "";

    public static DateTime ToBusinessLocalTime(this DateTime dt) => dt.Kind == DateTimeKind.Unspecified
        ? dt : TimeZoneInfo.ConvertTimeFromUtc(dt.ToUniversalTime(), BusinessTime.Zone);

    public static DateTime ToBusinessUtcTime(this DateTime localDate) => BusinessTime.ToUtc(localDate);

    public static bool IsInDateRange(this string? dateStr, DateTime? startDate, DateTime? endDate)
    {
        if (!BusinessTime.TryLocal(dateStr, out var local)) return false;
        if (startDate.HasValue && local < startDate.Value) return false;
        if (endDate.HasValue)
        {
            if (endDate.Value.TimeOfDay == TimeSpan.Zero)
            {
                if (local.Date > endDate.Value.Date) return false;
            }
            else if (local > endDate.Value) return false;
        }
        return true;
    }
}
