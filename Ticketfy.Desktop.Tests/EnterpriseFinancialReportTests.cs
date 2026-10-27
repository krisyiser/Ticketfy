using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.Views;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Theory]
    [InlineData("Efectivo", 10, 0, 0)]
    [InlineData("Tarjeta", 20, -10, 0)]
    [InlineData("Transferencia", 20, 0, -10)]
    public async Task FinancialReport_CreditRefundChangesReceiptsWithoutRepeatingRevenue(string method, int cash, int card, int transfer)
    {
        string source = await PaidCustomerReturnAsync();
        await Credits.RefundAsync(AccountKind.Customer, "C", source, 10m, method, "REF-REPORT", "Verificado", "REPORT-REFUND", true);
        var report = await new ExpenseService(new TestDbContextFactory(_options), _actor).GetFinancialSummaryAsync();
        Assert.Equal(10m, report.TotalRevenue);
        Assert.Equal(3m, report.TotalCostOfGoodsSold);
        Assert.Equal(7m, report.NetProfit);
        Assert.Equal((decimal)cash, report.CashRevenue);
        Assert.Equal((decimal)card, report.CardRevenue);
        Assert.Equal((decimal)transfer, report.TransferRevenue);
    }

    [Fact]
    public async Task FinancialReport_TransferCollectionIsNotAnotherSaleAndRefundOffsetsOnlyReceipts()
    {
        var sale = await Sales.SaveAsync(Sale("REPORT-CREDIT") with { IsCredit = true, CustomerId = "C", CashAmount = 0, PaymentMethod = "Crédito" });
        await Transfers.PrepareAsync("REPORT-ABONO", IncomingTransferService.CustomerPurpose, "C", 20m, "Abono");
        await Transfers.ConfirmAndApplyAsync("REPORT-ABONO", "BANK-REPORT-COLLECTION", true);
        await Sales.ProcessPartialReturnAsync(sale.Id, "P", 1, "Devuelto", operationId: "REPORT-RETURN");
        await Credits.RefundAsync(AccountKind.Customer, "C", sale.Id, 10m, "Transferencia", "BANK-REPORT-REFUND", "Verificado", "REPORT-REFUND", true);
        var report = await new ExpenseService(new TestDbContextFactory(_options), _actor).GetFinancialSummaryAsync();
        Assert.Equal(10m, report.TotalRevenue);
        Assert.Equal(7m, report.NetProfit);
        Assert.Equal(10m, report.TransferRevenue);
        Assert.Equal(0m, report.CashRevenue);
        Assert.Equal(0m, report.CardRevenue);
    }

    [Fact]
    public async Task FinancialReport_ShiftScopeExcludesOtherRegisterAndAssignsExpenseReversalToReceivingShift()
    {
        await Sales.SaveAsync(Sale("OWN-SALE"));
        var expenses = new ExpenseService(new TestDbContextFactory(_options), _actor);
        await expenses.CreateAsync(new ExpenseDto("OWN-EXP", "General", 5m, "", "Pago", "Efectivo", ""));
        await using var db = Db();
        var other = CommercialTestSession.Create(db, register: "OTHER-REPORT", userId: Guid.NewGuid());
        var otherSales = new SaleService(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService(), other);
        await otherSales.SaveAsync(Sale("OTHER-SALE"));
        var otherExpenses = new ExpenseService(new TestDbContextFactory(_options), other);
        await otherExpenses.CancelAsync("OWN-EXP", "Recuperado en otra caja", "OTHER-VOID", true);
        string otherShift = (await db.Shifts.SingleAsync(s => s.RegisterId == "OTHER-REPORT")).Id;
        var own = await expenses.GetFinancialSummaryAsync(shiftId: "SHIFT-1");
        var theirs = await expenses.GetFinancialSummaryAsync(shiftId: otherShift);
        var all = await expenses.GetFinancialSummaryAsync();
        Assert.Equal(20m, own.TotalRevenue); Assert.Equal(20m, theirs.TotalRevenue); Assert.Equal(40m, all.TotalRevenue);
        Assert.Equal(5m, own.TotalExpenses); Assert.Equal(-5m, theirs.TotalExpenses); Assert.Equal(0m, all.TotalExpenses);
        Assert.Equal(5m, own.CashExpenses); Assert.Equal(-5m, theirs.CashExpenses);
        Assert.Single(await expenses.GetAllAsync(shiftId: "SHIFT-1"));
        Assert.Empty(await expenses.GetAllAsync(shiftId: otherShift));
        Assert.Empty(await expenses.GetAllAsync(new DateTime(2000, 1, 1), new DateTime(2000, 1, 2)));
    }

    [AvaloniaFact]
    public async Task Ui_ExpensesOffersDistinctBankMethodsAndShowsOnlyActiveRegister()
    {
        await Sales.SaveAsync(Sale("UI-REPORT-OWN"));
        await using var db = Db();
        var other = CommercialTestSession.Create(db, register: "OTHER-UI", userId: Guid.NewGuid());
        await new SaleService(new TestDbContextFactory(_options), new CoOccurrenceQueue(), new SpyAuditService(), other)
            .SaveAsync(Sale("UI-REPORT-OTHER"));
        var factory = new TestDbContextFactory(_options);
        var model = new ExpensesViewModel(new ExpenseService(factory, _actor));
        var view = new ExpensesView();
        var window = Show(view, model);
        try
        {
            await model.LoadExpensesAsync();
            Assert.Equal(20m, model.TotalRevenue);
            Assert.Equal(new[] { "Efectivo", "Tarjeta", "Transferencia" }, model.PaymentMethods);
            model.ExpenseAmount = 5m; model.ExpenseDescription = "Internet"; model.PaymentMethod = "Transferencia"; model.ExpenseReference = "BANK-EXP";
            await ClickTwiceAsync(view, model.AddExpenseCommand);
            Assert.Single(await db.Expenses.ToListAsync());
            Assert.Equal(5m, model.TotalExpenses);
            Assert.Equal(20m, model.TotalEnCaja);
            await new ShiftService(factory, _actor).CloseAsync("SHIFT-1", 20m);
            await model.LoadExpensesAsync();
            Assert.Equal(0m, model.TotalRevenue);
            Assert.Empty(model.Expenses);
            Assert.Contains("No hay turno", model.PeriodSubtitleDisplay);
        }
        finally { window.Close(); }
    }
}
