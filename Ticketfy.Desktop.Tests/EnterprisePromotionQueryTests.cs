using System.Diagnostics;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Helpers;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Xunit.Abstractions;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterprisePromotionQueryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 1000)]
    [InlineData(true, 2)]
    public async Task PricingQuery_FiltersUnrelatedRulesBeforeMaterialization_OnEncryptedSyntheticDatabase(bool globalRules, int cartLines)
    {
        string path = Path.Combine(Path.GetTempPath(), "ticketfy-promotion-query-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder(CommercialDatabaseConnection.Create(path, Guid.NewGuid().ToString("N"))) { Pooling = false }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var materialization = new RuleMaterialization();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(materialization).Options;
            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync();
            string from = BusinessTime.Today.AddDays(-1).ToString("O");
            string to = BusinessTime.Today.AddDays(1).ToString("O");
            db.Promotions.AddRange(Enumerable.Range(0, 10000).Select(i => new PromotionEntity
            {
                Id = "RULE-" + i.ToString("D5"),
                Name = "Synthetic rule",
                TargetProductId = globalRules ? null : "OTHER-" + i,
                StartDate = from,
                EndDate = to,
                Priority = 10,
                DiscountValue = 10m
            }));
            db.Promotions.Add(new PromotionEntity { Id = "WINNER", Name = "Synthetic winner", TargetProductId = "P", Priority = -1, DiscountValue = 10m, StartDate = from, EndDate = to });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            PromotionTarget[] targets = [new("P", "General", 1), .. Enumerable.Range(1, cartLines - 1).Select(i => new PromotionTarget("NO-MATCH-" + i, "Different category", 1))];
            for (int warmup = 0; warmup < 3; warmup++) await PromotionRuleReader.ReadApplicableAsync(db, targets);
            materialization.Count = 0;
            const int samples = 100;
            var elapsed = new List<double>(samples);
            long allocatedBefore = GC.GetTotalAllocatedBytes();
            for (int sample = 0; sample < samples; sample++)
            {
                long start = Stopwatch.GetTimestamp();
                var selected = await PromotionRuleReader.ReadApplicableAsync(db, targets);
                elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Assert.Equal(globalRules ? "RULE-00000" : "WINNER", Assert.Single(selected).Id);
            }
            long allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
            elapsed.Sort();
            var plan = await materialization.ExplainAsync(connection);
            output.WriteLine("PROMOTION_QUERY_MEASUREMENT " + JsonSerializer.Serialize(new
            {
                Kind = "synthetic-encrypted-file",
                GlobalRules = globalRules,
                Rules = 10001,
                CartLines = targets.Length,
                Samples = samples,
                Warmup = 3,
                P50Milliseconds = elapsed[49],
                P95Milliseconds = elapsed[94],
                P99Milliseconds = elapsed[98],
                MaximumMilliseconds = elapsed[^1],
                AllocatedBytesForSamples = allocated,
                MaterializedRules = materialization.Count,
                Framework = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                LogicalProcessors = Environment.ProcessorCount,
                QueryPlan = plan,
                Certification = false
            }));
            Assert.Equal(samples, materialization.Count); // Only relevant candidates may cross into managed memory.
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE FOR ORDER BY", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task CandidateFilter_PreservesFullEvaluationWithLegacyUnicodeQuantityAndDateRules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options); await db.Database.MigrateAsync();
        var today = BusinessTime.Today;
        var random = new Random(1701);
        string[] products = ["P", "p", "LEGACY", "'quoted\"; SELECT", "UNICODE-Ñ", "NONE"];
        string[] categories = ["CAFÉ", "café", "ÜNIDAD", "unidad", "General", ""];
        var rules = Enumerable.Range(0, 600).Select(i => new PromotionEntity
        {
            Id = $"R-{i:D4}",
            Name = "Synthetic",
            Priority = random.Next(0, 50),
            MinQuantity = random.Next(1, 15),
            IsActive = i % 13 == 0 ? 0 : 1,
            TargetProductId = i % 4 == 0 ? products[random.Next(products.Length)] : null,
            TargetId = i % 4 == 1 ? products[random.Next(products.Length)] : null,
            TargetCategory = i % 4 == 2 ? categories[random.Next(categories.Length)] : "",
            StartDate = i % 17 == 0 ? "unknown" : today.AddDays(-1).ToString(i % 2 == 0 ? "O" : "dd/MM/yyyy HH:mm:ss"),
            EndDate = today.AddDays(i % 19 == 0 ? -1 : 1).ToString("O")
        }).ToList();
        db.Promotions.AddRange(rules); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var allActive = await PromotionRuleReader.ActiveQuery(db).ToListAsync();
        for (int round = 0; round < 30; round++)
        {
            var targets = Enumerable.Range(0, 20).Select(_ => new PromotionTarget(products[random.Next(products.Length)],
                categories[random.Next(categories.Length)], random.Next(1, 20))).ToArray();
            var expectedIds = targets.Select(t => allActive.FirstOrDefault(rule => PromotionRuleReader.Matches(rule, t))?.Id).ToHashSet();
            var expected = allActive.Where(rule => expectedIds.Contains(rule.Id)).Select(rule => rule.Id);
            var actual = await PromotionRuleReader.ReadApplicableAsync(db, targets);
            Assert.Equal(expected, actual.Select(rule => rule.Id));
        }
        // Binary product IDs, legacy IDs and Unicode category matching are distinct contracts.
        db.Promotions.RemoveRange(await db.Promotions.ToListAsync()); await db.SaveChangesAsync();
        db.Promotions.AddRange(
            new PromotionEntity { Id = "UPPER", TargetProductId = "P" },
            new PromotionEntity { Id = "QUOTED", TargetId = products[3] },
            new PromotionEntity { Id = "ACCENT", TargetCategory = "ÜNIDAD" });
        await db.SaveChangesAsync();
        Assert.Empty(await PromotionRuleReader.ReadApplicableAsync(db, [new("p", "none", 1)]));
        Assert.Equal("QUOTED", Assert.Single(await PromotionRuleReader.ReadApplicableAsync(db, [new(products[3], null, 1)])).Id);
        Assert.Equal("ACCENT", Assert.Single(await PromotionRuleReader.ReadApplicableAsync(db, [new("NONE", "ünidad", 1)])).Id);
    }

    [Theory]
    [InlineData("P", 0)]
    [InlineData("P", -1)]
    [InlineData("P", double.NaN)]
    [InlineData("P", double.PositiveInfinity)]
    [InlineData(" ", 1)]
    public async Task InvalidPricingTargets_AreRejectedBeforeReading(string id, double quantity)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        // There is deliberately no schema: validation must run before any SQL.
        await Assert.ThrowsAsync<InvalidOperationException>(() => PromotionRuleReader.ReadApplicableAsync(db, [new(id, null, quantity)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PromotionRuleReader.ReadApplicableAsync(db,
            Enumerable.Range(0, 1001).Select(i => new PromotionTarget("P" + i, null, 1)).ToArray()));
        Assert.Empty(await PromotionRuleReader.ReadApplicableAsync(db, []));
    }

    [Fact]
    public async Task PrecedenceIndex_UpgradesRepeatsAndRevertsWithoutChangingStoredRules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261027101149_IndexPurchaseFinancialEvidence";
        await migrator.MigrateAsync(previous);
        db.Promotions.AddRange(new PromotionEntity { Id = "LEGACY", TargetId = "P", Revision = 37, Priority = 2, DiscountValue = 7.25m },
            new PromotionEntity { Id = "DISABLED", IsActive = 0, TargetCategory = "Café", Revision = 23, StartDate = "unknown" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        string before = JsonSerializer.Serialize(await db.Promotions.AsNoTracking().OrderBy(p => p.Id).ToListAsync());
        for (int repeat = 0; repeat < 2; repeat++)
        {
            await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(before, JsonSerializer.Serialize(await db.Promotions.AsNoTracking().OrderBy(p => p.Id).ToListAsync()));
            Assert.Equal("LEGACY", Assert.Single(await PromotionRuleReader.ReadApplicableAsync(db, [new("P", null, 1)])).Id);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_promotions_isActive_priority_id'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            await migrator.MigrateAsync(previous);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            Assert.Equal(before, JsonSerializer.Serialize(await db.Promotions.AsNoTracking().OrderBy(p => p.Id).ToListAsync()));
        }
    }

    private sealed class RuleMaterialization : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int Count;
        private string _sql = "";
        private (string Name, object Value)[] _parameters = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("json_each", StringComparison.Ordinal))
            {
                _sql = command.CommandText;
                _parameters = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value!)).ToArray();
            }
            return ValueTask.FromResult(result);
        }
        public async Task<List<string>> ExplainAsync(SqliteConnection connection)
        {
            Assert.NotEmpty(_sql);
            await using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN " + _sql;
            foreach (var parameter in _parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>(); while (await reader.ReadAsync()) rows.Add(reader.GetString(3));
            return rows;
        }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is PromotionEntity) Count++;
            return entity;
        }
    }
}
