using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Helpers;

namespace Ticketfy.Data;

/// <summary>One frozen business calendar for SQL pagination and managed summaries of mixed historical dates.</summary>
internal sealed class BusinessDateQuery
{
    public DateTime Start { get; }
    public DateTime End { get; }
    public TimeZoneInfo Zone { get; } = BusinessTime.Zone;
    public string Lower { get; }
    public string Upper { get; }

    public BusinessDateQuery(DateTime start, DateTime end)
    {
        Start = start;
        End = end.TimeOfDay == TimeSpan.Zero ? end.Date.AddTicks(TimeSpan.TicksPerDay - 1) : end;
        if (Start > End) throw new InvalidOperationException("La fecha inicial no puede ser posterior a la final.");
        Lower = Start > DateTime.MinValue.AddDays(2) ? Start.Date.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        Upper = End < DateTime.MaxValue.AddDays(-3) ? End.Date.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "9999-99-99";
    }
    public void RegisterOn(AppDbContext db) => RegisterOn(db, Zone);
    public static void RegisterOn(AppDbContext db, TimeZoneInfo zone)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        connection.CreateFunction<string?, long?>("ticketfy_business_ticks",
            value => BusinessTime.TryLocal(value, out var local, zone) ? local.Ticks : null, isDeterministic: true);
        connection.CreateFunction<string?, long?>("ticketfy_instant_ticks",
            value => BusinessTime.TryInstant(value, out var instant, zone) ? instant.UtcTicks : null, isDeterministic: true);
    }
}
