using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task TransferPaging_AllPendingRowsRemainReachableAfterAnEarlierResolution()
    {
        await SeedTransferQueuesAsync(205);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.GetPendingAsync());
        var first = await Transfers.GetPendingPageAsync();
        Assert.Equal(205, first.TotalCount); Assert.Equal(100, first.Items.Count);
        await Transfers.CancelUnfundedAsync("TRANSFER-0000", "Verificado sin fondos", true);
        var second = await Transfers.GetPendingPageAsync(after: first.Next);
        var last = await Transfers.GetPendingPageAsync(after: second.Next);
        Assert.Equal(204, second.TotalCount); Assert.Equal("TRANSFER-0100", second.Items[0].OperationId);
        Assert.Equal(5, last.Items.Count); Assert.Null(last.Next);
        Assert.Equal(205, first.Items.Concat(second.Items).Concat(last.Items).Select(i => i.OperationId).Distinct().Count());
        var search = await Transfers.GetPendingPageAsync(search: " 0204 ");
        Assert.Equal("TRANSFER-0204", Assert.Single(search.Items).OperationId);
        Assert.Equal("SHIFT-1", search.Items[0].ShiftId);
        Assert.Equal(CommercialTestSession.UserId.ToString(), search.Items[0].CashierUserId);
        Assert.Empty((await Transfers.GetPendingPageAsync(search: "ABSENT")).Items);
    }

    [Fact]
    public async Task TransferPaging_ReceiptHistoryHasNoSilentCutoffAndRetainsAllRegisterAttribution()
    {
        await SeedTransferQueuesAsync(205, receipts: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transfers.GetReceiptsAsync());
        var first = await Transfers.GetReceiptsPageAsync();
        var second = await Transfers.GetReceiptsPageAsync(after: first.Next);
        var last = await Transfers.GetReceiptsPageAsync(after: second.Next);
        Assert.Equal(205, first.TotalCount); Assert.Equal(100, first.Items.Count); Assert.Equal(5, last.Items.Count);
        Assert.Null(last.Next);
        Assert.Equal(205, first.Items.Concat(second.Items).Concat(last.Items).Select(i => i.OperationId).Distinct().Count());
        Assert.Contains(first.Items, i => i.RegisterId == "OTHER-REGISTER");
        Assert.Equal("TRANSFER-0204", Assert.Single((await Transfers.GetReceiptsPageAsync(search: "BANK-0204")).Items).OperationId);
        Assert.Single((await Transfers.GetReceiptsPageAsync(search: "REFUND-0001")).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferPaging_InvalidSizeSearchOrForeignCursorIsRejected(bool receipts)
    {
        await SeedTransferQueuesAsync(2, receipts);
        async Task Read(int size, string? search = null, Ticketfy.Data.Dtos.TransferQueueCursor? after = null)
        {
            if (receipts) await Transfers.GetReceiptsPageAsync(size, search, after);
            else await Transfers.GetPendingPageAsync(size, search, after);
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Read(0));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Read(201));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Read(1, "bad\n"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Read(1, new string('x', 129)));
        var cursor = receipts ? (await Transfers.GetReceiptsPageAsync(1)).Next! : (await Transfers.GetPendingPageAsync(1)).Next!;
        foreach (var invalid in new[] { cursor with { Receipts = !receipts }, cursor with { RegisterId = "OTHER" },
            cursor with { UserId = "OTHER" }, cursor with { Supervisory = !cursor.Supervisory },
            cursor with { Search = "OTHER" }, cursor with { AfterOperationId = "" } })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Read(1, after: invalid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferPaging_ReadsOnlyBoundedHeadersAndNeverIntentNotes(bool receipts)
    {
        await SeedTransferQueuesAsync(1001, receipts);
        var observer = new TransferReadObserver();
        var options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var service = new IncomingTransferService(new TestDbContextFactory(options), _actor);
        if (receipts)
        {
            var page = await service.GetReceiptsPageAsync(17);
            Assert.Equal(1001, page.TotalCount); Assert.Equal(17, page.Items.Count);
        }
        else
        {
            var page = await service.GetPendingPageAsync(17);
            Assert.Equal(1001, page.TotalCount); Assert.Equal(17, page.Items.Count);
        }
        Assert.False(observer.Unbounded); Assert.False(observer.ReadsNotes); Assert.Contains(18, observer.Limits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferPaging_CountAndRowsUseOneSnapshot(bool receipts)
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, actor) =>
        {
            await using (var db = new AppDbContext(options)) { AddTransferQueueRow(db, 0, receipts); await db.SaveChangesAsync(); }
            bool wrote = false;
            var observer = new TransferReadObserver(async command =>
            {
                if (wrote || !command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) return;
                wrote = true;
                await using var writer = new AppDbContext(options);
                AddTransferQueueRow(writer, 1, receipts); await writer.SaveChangesAsync();
            });
            var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(observer).Options);
            var service = new IncomingTransferService(factory, actor);
            if (receipts)
            {
                var old = await service.GetReceiptsPageAsync(); Assert.Equal(1, old.TotalCount); Assert.Single(old.Items);
                Assert.Equal(2, (await service.GetReceiptsPageAsync()).TotalCount);
            }
            else
            {
                var old = await service.GetPendingPageAsync(); Assert.Equal(1, old.TotalCount); Assert.Single(old.Items);
                Assert.Equal(2, (await service.GetPendingPageAsync()).TotalCount);
            }
            Assert.True(wrote);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferPaging_RechecksAuthorizationBeforeReturningRows(bool receipts)
    {
        await SeedTransferQueuesAsync(1, receipts);
        var engine = new UserRolePermissionEngine(); var session = new SessionManager();
        Ticketfy.Core.Models.UserModel replacement;
        await using (var db = Db())
        {
            session.StartSession(engine.MapToModel(await db.Users.SingleAsync()));
            var other = Guid.NewGuid(); CommercialTestSession.Create(db, false, userId: other);
            replacement = engine.MapToModel(await db.Users.SingleAsync(u => u.Id == other));
        }
        var observer = new TransferReadObserver(command =>
        {
            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) session.StartSession(replacement);
            return Task.CompletedTask;
        });
        var service = new IncomingTransferService(new TestDbContextFactory(
            new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options),
            new CommercialOperationContext(session, engine, "TEST-REGISTER"));
        if (receipts) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetReceiptsPageAsync());
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetPendingPageAsync());
    }

    private async Task SeedTransferQueuesAsync(int count, bool receipts = false)
    {
        await using var db = Db();
        for (int i = 0; i < count; i++) AddTransferQueueRow(db, i, receipts);
        await db.SaveChangesAsync();
    }
    private static void AddTransferQueueRow(AppDbContext db, int i, bool receipts)
    {
        string id = $"TRANSFER-{i:D4}";
        if (receipts) db.IncomingTransferReceipts.Add(new()
        {
            OperationId = id,
            Purpose = IncomingTransferService.WalletPurpose,
            DocumentId = "G",
            Amount = 10m,
            CashierUserId = CommercialTestSession.UserId.ToString(),
            RegisterId = i % 2 == 0 ? "TEST-REGISTER" : "OTHER-REGISTER",
            ShiftId = "SHIFT-1",
            ExternalReference = $"BANK-{i:D4}",
            RefundReference = i == 1 ? "REFUND-0001" : null,
            Outcome = i == 1 ? TransferOutcome.RefundedExternally : TransferOutcome.Applied
        });
        else db.IncomingTransferIntents.Add(new()
        {
            OperationId = id,
            Purpose = IncomingTransferService.WalletPurpose,
            DestinationId = "G",
            Amount = 10m,
            Notes = new string('n', 4096),
            CashierUserId = CommercialTestSession.UserId.ToString(),
            RegisterId = "TEST-REGISTER",
            ShiftId = "SHIFT-1"
        });
    }

    private sealed class TransferReadObserver(Func<DbCommand, Task>? afterRead = null) : DbCommandInterceptor
    {
        public bool Unbounded, ReadsNotes;
        public List<int> Limits { get; } = [];
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"incoming_transfer_", StringComparison.Ordinal))
            {
                string sql = command.CommandText.TrimStart();
                bool scalar = sql.StartsWith("SELECT COUNT(", StringComparison.OrdinalIgnoreCase) ||
                    sql.StartsWith("SELECT EXISTS", StringComparison.OrdinalIgnoreCase);
                Unbounded |= !scalar && !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);
                ReadsNotes |= sql.Contains("\"Notes\"", StringComparison.Ordinal) || sql.Contains("\"notes\"", StringComparison.Ordinal);
                if (!scalar) Limits.AddRange(command.Parameters.Cast<DbParameter>().Where(p => p.Value is int).Select(p => (int)p.Value!));
                if (afterRead is not null) await afterRead(command);
            }
            return result;
        }
    }
}
