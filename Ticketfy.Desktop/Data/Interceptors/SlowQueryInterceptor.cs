using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace Ticketfy.Data.Interceptors;

public class SlowQueryInterceptor : DbCommandInterceptor
{
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (eventData.Duration.TotalMilliseconds > 50)
        {
            Log.Warning("Slow query detected ({DurationMs} ms): {CommandText}",
                eventData.Duration.TotalMilliseconds, command.CommandText);
        }
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        if (eventData.Duration.TotalMilliseconds > 50)
        {
            Log.Warning("Slow query detected ({DurationMs} ms): {CommandText}",
                eventData.Duration.TotalMilliseconds, command.CommandText);
        }
        return base.ReaderExecuted(command, eventData, result);
    }
}
