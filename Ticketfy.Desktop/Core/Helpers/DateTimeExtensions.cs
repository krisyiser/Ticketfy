using System;

namespace Ticketfy.Core.Helpers;

public static class DateTimeExtensions
{
    public static string ToLocalDisplayString(string? dateString, string format = "g")
    {
        if (string.IsNullOrWhiteSpace(dateString)) return string.Empty;

        if (DateTimeOffset.TryParse(dateString, out var dto))
        {
            return dto.LocalDateTime.ToString(format);
        }

        if (DateTime.TryParse(dateString, out var dt))
        {
            return dt.ToLocalTime().ToString(format);
        }

        return dateString;
    }

    public static DateTime ToBusinessLocalTime(this DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Local)
        {
            return dt;
        }

        if (dt.Kind == DateTimeKind.Utc)
        {
            return dt.ToLocalTime();
        }

        return dt;
    }

    public static DateTime ToBusinessUtcTime(this DateTime localDate)
    {
        if (localDate.Kind == DateTimeKind.Utc)
        {
            return localDate;
        }

        return localDate.ToUniversalTime();
    }

    public static bool IsInDateRange(this string? dateStr, DateTime? startDate, DateTime? endDate)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return false;

        DateTime localDt;
        if (DateTimeOffset.TryParse(dateStr, out var dto))
        {
            localDt = dto.LocalDateTime;
        }
        else if (DateTime.TryParse(dateStr, out var dt))
        {
            localDt = dt.ToLocalTime();
        }
        else
        {
            return false;
        }

        if (startDate.HasValue && localDt < startDate.Value) return false;
        if (endDate.HasValue)
        {
            if (endDate.Value.TimeOfDay == TimeSpan.Zero)
            {
                if (localDt >= endDate.Value.Date.AddDays(1)) return false;
            }
            else if (localDt > endDate.Value) return false;
        }

        return true;
    }
}
