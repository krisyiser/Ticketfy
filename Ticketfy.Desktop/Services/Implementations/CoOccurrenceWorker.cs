using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ticketfy.Data;
using Ticketfy.Core.Operations;

namespace Ticketfy.Services.Implementations;

public sealed class CoOccurrenceWorker(CoOccurrenceQueue queue, IDbContextFactory<AppDbContext> factory,
    TimeProvider? clock = null, MaintenanceHealth? health = null) : OwnedBackgroundWorker(health, MaintenanceTask.Recommendations)
{
    internal const int PairBatchSize = 256;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                BeginCycle();
                while (await ProcessBatchAsync(token).ConfigureAwait(false))
                    await Task.Delay(TimeSpan.FromMilliseconds(25), _clock, token).ConfigureAwait(false);
                ClearFailure();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { RecordFailure(ex); }
            await queue.WaitAsync(TimeSpan.FromSeconds(30), _clock, token).ConfigureAwait(false);
        }
    }

    internal async Task<bool> ProcessBatchAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        var job = await db.RecommendationJobs.Where(j => !j.IsCompleted).OrderBy(j => j.Id).FirstOrDefaultAsync(token).ConfigureAwait(false);
        if (job is null) return false;
        var ids = db.SaleLines.AsNoTracking().Where(l => l.SaleId == job.SaleId).Select(l => l.ProductId).Distinct().OrderBy(id => id);
        int count = await ids.CountAsync(token).ConfigureAwait(false);
        if (count < 2 || job.NextLeft < 0 || job.NextLeft >= count - 1 || job.NextRight <= job.NextLeft || job.NextRight >= count)
            throw new InvalidDataException("El cursor de recomendaciones requiere revisión.");
        var pairs = new List<(string A, string B)>(PairBatchSize);
        while (job.NextLeft < count - 1 && pairs.Count < PairBatchSize)
        {
            token.ThrowIfCancellationRequested();
            string left = await ids.Skip(job.NextLeft).FirstAsync(token).ConfigureAwait(false);
            var right = await ids.Skip(job.NextRight).Take(PairBatchSize - pairs.Count).ToListAsync(token).ConfigureAwait(false);
            foreach (string id in right)
                pairs.Add(string.CompareOrdinal(left, id) < 0 ? (left, id) : (id, left));
            job.NextRight += right.Count;
            if (job.NextRight >= count) { job.NextLeft++; job.NextRight = job.NextLeft + 1; }
        }
        if (pairs.Count > 0)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = tx.GetDbTransaction();
            var values = new List<string>(pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
            {
                values.Add($"(@a{i},@b{i},1)");
                var a = command.CreateParameter(); a.ParameterName = $"@a{i}"; a.Value = pairs[i].A; command.Parameters.Add(a);
                var b = command.CreateParameter(); b.ParameterName = $"@b{i}"; b.Value = pairs[i].B; command.Parameters.Add(b);
            }
            command.CommandText = "INSERT INTO co_ocurrencia (producto_a,producto_b,frecuencia) VALUES "
                + string.Join(",", values) + """
                 ON CONFLICT(producto_a,producto_b) DO UPDATE SET frecuencia=co_ocurrencia.frecuencia+1
                 WHERE co_ocurrencia.frecuencia BETWEEN 0 AND 2147483646;
                """;
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != pairs.Count)
                throw new InvalidDataException("Una frecuencia de recomendaciones excede el rango; no se avanzó el lote.");
        }
        job.IsCompleted = job.NextLeft >= count - 1;
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await tx.CommitAsync(token).ConfigureAwait(false);
        return true;
    }
}
