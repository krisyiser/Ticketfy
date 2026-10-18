using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Serilog;

namespace Ticketfy.Services.Implementations;

public class CoOccurrenceWorker
{
    private readonly CoOccurrenceQueue _queue;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public CoOccurrenceWorker(CoOccurrenceQueue queue, IDbContextFactory<AppDbContext> contextFactory)
    {
        _queue = queue;
        _contextFactory = contextFactory;
    }

    public Task StartAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => ExecuteAsync(stoppingToken), stoppingToken);
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var productIds in _queue.Queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessCombinationsAndSaveToDb(productIds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing co-occurrences in background worker");
            }
        }
    }

    private async Task ProcessCombinationsAndSaveToDb(List<string> productIds)
    {
        if (productIds.Count <= 1) return;

        using var _ctx = await _contextFactory.CreateDbContextAsync();

        for (int i = 0; i < productIds.Count; i++)
        {
            for (int j = i + 1; j < productIds.Count; j++)
            {
                string idA = productIds[i];
                string idB = productIds[j];
                if (string.CompareOrdinal(idA, idB) > 0)
                {
                    (idA, idB) = (idB, idA);
                }

                var pair = await _ctx.CoOccurrences.FirstOrDefaultAsync(c => c.ProductoA == idA && c.ProductoB == idB);
                if (pair is null)
                {
                    _ctx.CoOccurrences.Add(new CoOccurrenceEntity { ProductoA = idA, ProductoB = idB, Frecuencia = 1 });
                }
                else
                {
                    pair.Frecuencia++;
                }
            }
        }

        await DbResilienceHelper.ExecuteWithRetryAsync(async () => await _ctx.SaveChangesAsync());
    }
}
