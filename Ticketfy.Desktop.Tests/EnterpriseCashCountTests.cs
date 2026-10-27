using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ticketfy.Views.Dialogs;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData(false, -1, 0)]
    [InlineData(true, -1, 0)]
    [InlineData(false, 1, 19)]
    [InlineData(true, 1, 19)]
    public async Task CashCount_RejectsNegativeOrInconsistentDenominationsWithoutAnyCommit(bool final, int count20, decimal total)
    {
        var cashup = new CashupEntity { Id = "BAD-COUNT", OperationId = "BAD-COUNT", Type = final ? "Final" : "Parcial", Count20 = count20, ClosedCashAmount = total };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", cashup));
        await using var db = Db();
        Assert.Empty(await db.Cashups.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
        Assert.Empty(await db.TransactionJournal.ToListAsync());
        Assert.Equal(1, (await db.Shifts.SingleAsync()).IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashCount_RetryCannotChangeDenominationsEvenWithSameTotal(bool final)
    {
        var cashup = new CashupEntity { Id = "COUNT-EVIDENCE", OperationId = "COUNT-EVIDENCE", Type = final ? "Final" : "Parcial", Count20 = 1, ClosedCashAmount = 20m };
        await ShiftsService().RecordCountAsync("SHIFT-1", cashup);
        await ShiftsService().RecordCountAsync("SHIFT-1", cashup);
        cashup.Count20 = 0;
        cashup.Count10 = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", cashup));
        await using var db = Db();
        var saved = Assert.Single(await db.Cashups.ToListAsync());
        Assert.Equal(1, saved.Count20);
        Assert.Equal(0, saved.Count10);
        Assert.Single(await db.CommandReceipts.ToListAsync());
    }

    [AvaloniaTheory]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("999999999999999999999999999999")]
    [InlineData("abc")]
    public async Task CashCount_UiRejectsInvalidInputInsteadOfSilentlySavingZero(string value)
    {
        await using var db = Db();
        var vm = new CashupDialogViewModel(ShiftsService()) { Count20 = value };
        await vm.SaveCashupCommand.ExecuteAsync(null);
        Assert.True(vm.IsFeedbackError);
        Assert.Contains("enteras", vm.FeedbackMessage);
        Assert.Empty(await db.Cashups.ToListAsync());
        Assert.Equal(1, (await db.Shifts.SingleAsync()).IsOpen);
    }

    [AvaloniaFact]
    public async Task CashCount_UiRecalculatesCommittedTotalFromExactlyThePersistedDenominations()
    {
        await using var db = Db();
        var vm = new CashupDialogViewModel(ShiftsService()) { Count20 = " 1 ", Count1 = "", Count050 = "3" };
        vm.TotalPhysicalCash = 999m; // A stale/tampered display value is not command input.
        await vm.SaveCashupCommand.ExecuteAsync(null);
        Assert.False(vm.IsFeedbackError);
        var saved = Assert.Single(await db.Cashups.ToListAsync());
        Assert.Equal(21.50m, saved.ClosedCashAmount);
        Assert.Equal(1, saved.Count20);
        Assert.Equal(3, saved.Count050);
        Assert.Equal(0, saved.Count1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashCount_LegacyCommandRetryPreservesFingerprintAndReturnsOriginalEvidence(bool final)
    {
        await Sales.SaveAsync(Sale("COUNT-LEGACY-SALE"));
        var cashup = new CashupEntity { Id = "ORIGINAL", OperationId = "COUNT-LEGACY", Type = final ? "Final" : "Parcial", Count20 = 1, ClosedCashAmount = 20m };
        await ShiftsService().RecordCountAsync("SHIFT-1", cashup);
        await using var db = Db();
        var receipt = await db.CommandReceipts.AsNoTracking().SingleAsync(c => c.OperationId == "COUNT-LEGACY");
        // Reconstruct the pre-upgrade fingerprint solely in this disposable fixture.
        receipt.Command = final ? "SHIFT_CLOSE" : "COUNT";
        object[] legacyValues = [receipt.Command, CommercialTestSession.UserId.ToString(), "TEST-REGISTER", "SHIFT-1", "SHIFT-1", 20m, ""];
        receipt.Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(legacyValues.Select(v =>
        {
            string text = v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString()!;
            return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + text;
        })))));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE command_receipts SET Command={receipt.Command}, Fingerprint={receipt.Fingerprint} WHERE OperationId='COUNT-LEGACY';");
        string fingerprint = receipt.Fingerprint;
        var replay = new CashupEntity { Id = "RETRY-DISPLAY-ID", OperationId = "COUNT-LEGACY", Type = cashup.Type, Count20 = 1, ClosedCashAmount = 20m };
        await ShiftsService().RecordCountAsync("SHIFT-1", replay);
        Assert.Equal("ORIGINAL", replay.Id);
        Assert.Equal(cashup.Timestamp, replay.Timestamp);
        Assert.Equal(20m, replay.TheoreticalCash);
        Assert.Equal(cashup.CashierName, replay.CashierName);
        Assert.Equal(fingerprint, (await db.CommandReceipts.AsNoTracking().SingleAsync(c => c.OperationId == "COUNT-LEGACY")).Fingerprint);
        if (final) Assert.True(Assert.Single(await Reconciliation.ReconcileAsync(BusinessTime.Today)).IsBalanced);
        replay.Count20 = 0;
        replay.Count10 = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", replay));
        Assert.Single(await db.Cashups.ToListAsync());
    }

    [Theory]
    [InlineData("UPDATE cashups SET count_20=0, count_10=2;")]
    [InlineData("UPDATE cashups SET count_20=0;")]
    [InlineData("UPDATE cashups SET count_20=-1;")]
    public async Task CashCount_ReconciliationDetectsChangedPiecesEvenWhenTotalIsUnchanged(string corruption)
    {
        await Sales.SaveAsync(Sale("COUNT-AUDIT-SALE"));
        await ShiftsService().RecordCountAsync("SHIFT-1", new CashupEntity
        { Id = "COUNT-AUDIT", OperationId = "COUNT-AUDIT", Type = "Final", Count20 = 1, ClosedCashAmount = 20m });
        Assert.True(Assert.Single(await Reconciliation.ReconcileAsync(BusinessTime.Today)).IsBalanced);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync(corruption);
        var result = Assert.Single(await Reconciliation.ReconcileAsync(BusinessTime.Today));
        Assert.False(result.IsBalanced);
        Assert.Contains(result.Issues, i => i.Code == "CLOSE_EVIDENCE");
    }

    [Fact]
    public async Task CashCount_RejectsMissingStoredEvidenceAndInvalidTypeOrMoneyWithoutCreatingAnotherCut()
    {
        var cashup = new CashupEntity { Id = "BROKEN-COUNT", OperationId = "BROKEN-COUNT", Type = "Parcial" };
        await ShiftsService().RecordCountAsync("SHIFT-1", cashup);
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM cashups;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", cashup));
        cashup.OperationId = "NEW-COUNT";
        cashup.Type = "unknown";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", cashup));
        cashup.Type = "Parcial";
        cashup.ClosedCashAmount = decimal.MaxValue;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", cashup));
        cashup.ClosedCashAmount = .001m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShiftsService().RecordCountAsync("SHIFT-1", cashup));
        Assert.Empty(await db.Cashups.ToListAsync());
        Assert.Single(await db.CommandReceipts.ToListAsync());
    }

    [Fact]
    public void CashCount_DenominationParserIsExactAndAggregateEvidenceIsExplicit()
    {
        Assert.False(CashDenominationCount.TryParse(["0"], out _));
        Assert.True(CashDenominationCount.TryParse(["1", "1", "1", "1", "1", "1", "1", "1", "1", "1", "1"], out var pieces));
        Assert.Equal(1888.50m, pieces!.Total);
        Assert.True(pieces.IsConsistentWith(1888.50m));
        Assert.False(pieces.IsConsistentWith(1888m));
        Assert.Contains("sin detalle", new CashupEntity { ClosedCashAmount = 1m }.DenominationStatus);
        Assert.Contains("inconsistente", new CashupEntity { ClosedCashAmount = 1m, Count20 = 1 }.DenominationStatus);
        Assert.Equal("Conteo por denominaciones", new CashupEntity { ClosedCashAmount = 20m, Count20 = 1 }.DenominationStatus);
    }

    [AvaloniaFact]
    public async Task CashCount_BlindRecoveryDoesNotRevealExpectedAmountsOrAllowPartialCut()
    {
        await Sales.SaveAsync(Sale("BLIND-SALE"));
        await using var db = Db();
        var vm = new CashupDialogViewModel(ShiftsService(), isFinalZCut: true, isBlindMode: true) { Count20 = "1" };
        var window = Show(new CashupDialog(), vm);
        try
        {
            Assert.Equal(0m, vm.TheoreticalCash);
            Assert.Equal(0m, vm.TotalIngresosShift);
            var theoretical = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Text == "2. Ingresos");
            Assert.False(theoretical.IsEffectivelyVisible);
            vm.IsPartialMode = true; // Recovery must remain final even if a bound value changes.
            await vm.SaveCashupCommand.ExecuteAsync(null);
            Assert.False(vm.IsFeedbackError);
            var cut = Assert.Single(await db.Cashups.ToListAsync());
            Assert.Equal("Final", cut.Type);
            Assert.Equal(20m, cut.TheoreticalCash);
            Assert.Equal(0m, cut.Difference);
            Assert.Equal(0, (await db.Shifts.AsNoTracking().SingleAsync()).IsOpen);
        }
        finally { window.Close(); }
    }
}
