using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private async Task<RefundQueuePage<string>> ReadRefundPageAsync(bool credit, int size = 100, string? search = null, RefundQueueCursor? after = null)
    {
        if (credit)
        {
            var p = await Credits.GetPendingRefundsPageAsync(size, search, after);
            return new(p.Items.Select(i => i.OperationId).ToList(), p.TotalCount, p.Next);
        }
        var bank = await Reconciliation.GetPendingRefundsPageAsync(size, search, after);
        return new(bank.Items.Select(i => i.Id).ToList(), bank.TotalCount, bank.Next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundPaging_AllRowsReachableDespiteEarlierResolution(bool credit)
    {
        await SeedRefundQueuesAsync(205, credit);
        var first = await ReadRefundPageAsync(credit);
        if (credit) await Credits.ResolveRefundAsync("REFUND-0000", false, null, "Comprobado sin fondos", true);
        else await Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-REFUND", "BANK-SETTLE");
        var second = await ReadRefundPageAsync(credit, after: first.Next);
        var last = await ReadRefundPageAsync(credit, after: second.Next);
        Assert.Equal(205, first.TotalCount); Assert.Equal(204, second.TotalCount);
        Assert.Equal("REFUND-0100", second.Items[0]); Assert.Equal(5, last.Items.Count); Assert.Null(last.Next);
        Assert.Equal(205, first.Items.Concat(second.Items).Concat(last.Items).Distinct().Count());
        Assert.Equal("REFUND-0204", Assert.Single((await ReadRefundPageAsync(credit, search: " 0204 ")).Items));
        Assert.Empty((await ReadRefundPageAsync(credit, search: "ABSENT")).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundPaging_InvalidInputsAndForeignCursorsAreRejected(bool credit)
    {
        await SeedRefundQueuesAsync(2, credit);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ReadRefundPageAsync(credit, 0));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ReadRefundPageAsync(credit, 201));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ReadRefundPageAsync(credit, search: "x\n"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ReadRefundPageAsync(credit, search: new string('x', 129)));
        var cursor = (await ReadRefundPageAsync(credit, 1)).Next!;
        foreach (var invalid in new[] { cursor with { AccountCredit = !credit }, cursor with { RegisterId = "OTHER" },
            cursor with { UserId = "OTHER" }, cursor with { Search = "OTHER" }, cursor with { AfterId = "" } })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => ReadRefundPageAsync(credit, after: invalid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundPaging_HeadersAreBoundedAndExcludeFreeText(bool credit)
    {
        await SeedRefundQueuesAsync(1001, credit);
        var observer = new RefundReadObserver();
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        var page = await ReadRefundPageAsync(credit, 17);
        Assert.Equal(1001, page.TotalCount); Assert.Equal(17, page.Items.Count);
        Assert.False(observer.Unbounded); Assert.False(observer.ReadsReason); Assert.Contains(18, observer.Limits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundPaging_SessionChangeCannotDiscloseRows(bool credit)
    {
        await SeedRefundQueuesAsync(1, credit);
        var engine = new UserRolePermissionEngine(); var session = new SessionManager();
        Ticketfy.Core.Models.UserModel replacement;
        await using (var db = Db())
        {
            session.StartSession(engine.MapToModel(await db.Users.SingleAsync()));
            var other = Guid.NewGuid(); CommercialTestSession.Create(db, false, userId: other);
            replacement = engine.MapToModel(await db.Users.SingleAsync(u => u.Id == other));
        }
        var observer = new RefundReadObserver(command =>
        {
            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) session.StartSession(replacement);
            return Task.CompletedTask;
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        _actor = new CommercialOperationContext(session, engine, "TEST-REGISTER");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ReadRefundPageAsync(credit));
    }

    [Fact]
    public async Task RefundPaging_CreditCountAndRowsUseOneSnapshotAndRegisterScope()
    {
        await EnterpriseCatalogSnapshotTests.WithSyntheticDatabase(async (options, _, actor) =>
        {
            async Task Add(string id, string register)
            {
                await using var db = new AppDbContext(options);
                db.CreditRefundRequests.Add(new() { OperationId = id, RegisterId = register, Amount = 10 });
                await db.SaveChangesAsync();
            }
            await Add("A", "TEST-REGISTER"); await Add("FOREIGN", "OTHER");
            bool wrote = false;
            var observer = new RefundReadObserver(async command =>
            {
                if (wrote || !command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) return;
                wrote = true; await Add("B", "TEST-REGISTER");
            });
            var service = new AccountCreditSettlementService(new TestDbContextFactory(
                new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(observer).Options), actor);
            var old = await service.GetPendingRefundsPageAsync();
            Assert.True(wrote); Assert.Equal(1, old.TotalCount); Assert.Equal("A", Assert.Single(old.Items).OperationId);
            var current = await service.GetPendingRefundsPageAsync();
            Assert.Equal(2, current.TotalCount); Assert.DoesNotContain(current.Items, i => i.RegisterId == "OTHER");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundIntegrity_SessionEndingDuringResolutionRollsBackMoney(bool credit)
    {
        if (credit)
        {
            string source = await PaidCustomerReturnAsync();
            await Credits.PrepareRefundAsync(Ticketfy.Data.Entities.AccountKind.Customer, "C", source, 10m, "Efectivo", "Prueba", "REFUND-0000");
        }
        else await SeedRefundQueuesAsync(1, false);
        var engine = new UserRolePermissionEngine(); var session = new SessionManager();
        await using (var setup = Db()) session.StartSession(engine.MapToModel(await setup.Users.SingleAsync()));
        bool ended = false;
        var observer = new RefundReadObserver(command =>
        {
            if (!ended) { ended = true; session.ClearSession(); }
            return Task.CompletedTask;
        });
        _options = new DbContextOptionsBuilder<AppDbContext>(_options).AddInterceptors(observer).Options;
        _actor = new CommercialOperationContext(session, engine, "TEST-REGISTER");
        if (credit) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Credits.ResolveRefundAsync("REFUND-0000", true, null, "Fondos", true));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Reconciliation.SettleRefundAsync("REFUND-0000", "BANK-OUT", "RESOLVE"));
        Assert.True(ended);
        await using var db = Db();
        Assert.Empty(await db.RefundSettlements.ToListAsync());
        Assert.Empty(await db.AccountCreditSettlements.ToListAsync());
        Assert.Empty(await db.CreditRefundResolutions.ToListAsync());
    }

    private sealed class RefundReadObserver(Func<DbCommand, Task>? afterRead = null) : DbCommandInterceptor
    {
        public bool Unbounded, ReadsReason;
        public List<int> Limits { get; } = [];
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"credit_refund_requests\"", StringComparison.Ordinal) ||
                command.CommandText.Contains("FROM \"payment_refunds\"", StringComparison.Ordinal))
            {
                string sql = command.CommandText.TrimStart();
                bool scalar = sql.StartsWith("SELECT COUNT(", StringComparison.OrdinalIgnoreCase) ||
                    sql.StartsWith("SELECT EXISTS", StringComparison.OrdinalIgnoreCase);
                Unbounded |= !scalar && !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);
                ReadsReason |= sql.Contains("\"Reason\"", StringComparison.Ordinal);
                if (!scalar) Limits.AddRange(command.Parameters.Cast<DbParameter>().Where(p => p.Value is int).Select(p => (int)p.Value!));
                if (afterRead is not null) await afterRead(command);
            }
            return result;
        }
    }
}
