using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Operations;

namespace Ticketfy.Data.Interceptors;

/// <summary>Checks the runtime commercial factory at writes and before commit; reads and rollback remain available.</summary>
internal static class MaintenanceWriteGuard
{
    internal static IInterceptor[] Create(MaintenanceHealth health) =>
        [new SaveGuard(health), new TransactionGuard(health), new CommandGuard(health)];

    private sealed class SaveGuard(MaintenanceHealth health) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { health.RequireWritesAllowed(); return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { health.RequireWritesAllowed(); return ValueTask.FromResult(result); }
    }
    private sealed class TransactionGuard(MaintenanceHealth health) : DbTransactionInterceptor
    {
        public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
        { health.RequireWritesAllowed(); return result; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { health.RequireWritesAllowed(); return ValueTask.FromResult(result); }
    }
    private sealed class CommandGuard(MaintenanceHealth health) : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { health.RequireWritesAllowed(); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { health.RequireWritesAllowed(); return ValueTask.FromResult(result); }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { if (eventData.CommandSource == CommandSource.SaveChanges) health.RequireWritesAllowed(); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (eventData.CommandSource == CommandSource.SaveChanges) health.RequireWritesAllowed(); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { if (eventData.CommandSource == CommandSource.SaveChanges) health.RequireWritesAllowed(); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { if (eventData.CommandSource == CommandSource.SaveChanges) health.RequireWritesAllowed(); return ValueTask.FromResult(result); }
    }
}
