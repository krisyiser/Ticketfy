using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ticketfy.Data;

/// <summary>A single read view across a report's queries, without taking SQLite's writer reservation.</summary>
internal sealed class ReadSnapshot : IAsyncDisposable
{
    private readonly AppDbContext _db;
    private readonly SqliteTransaction _transaction;
    private readonly IDbContextTransaction _efTransaction;
    private ReadSnapshot(AppDbContext db, SqliteTransaction transaction, IDbContextTransaction efTransaction)
    { _db = db; _transaction = transaction; _efTransaction = efTransaction; }

    public static async Task<ReadSnapshot> BeginAsync(AppDbContext db)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Ya existe una transacción en este contexto.");
        await db.Database.OpenConnectionAsync();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            var ef = await db.Database.UseTransactionAsync(transaction)
                ?? throw new InvalidOperationException("No se pudo iniciar la lectura consistente.");
            return new(db, transaction, ef);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _efTransaction.DisposeAsync();
        await _transaction.DisposeAsync();
        await _db.Database.CloseConnectionAsync();
    }
}
