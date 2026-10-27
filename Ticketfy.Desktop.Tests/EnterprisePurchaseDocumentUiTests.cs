using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Suppliers;
using Ticketfy.Views.Suppliers;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task PurchaseFinanceUi_ShowsVerifiedBalancesAndRefreshesReturnsAfterCommit()
    {
        string id = await ReceiveFinancialPurchaseAsync(paid: 1m);
        var service = PurchaseFinanceService();
        var model = new PurchaseHistoryViewModel(service);
        var view = new PurchaseHistoryView(); var window = Show(view, model);
        try
        {
            await model.LoadPurchasesAsync();
            await model.ViewPurchaseDetailCommand.ExecuteAsync(Assert.Single(model.Purchases));
            await model.LoadFinancialCommand.ExecuteAsync(null);
            Assert.Equal(2, model.FinancialEntries.Count);
            Assert.Equal(5m, model.SelectedPurchaseForDetail!.Financial!.Balance);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Pendiente de pago");
            await model.NextFinancialCommand.ExecuteAsync(null); Assert.False(model.HasPreviousFinancial);
            await model.PreviousFinancialCommand.ExecuteAsync(null);
            var control = EnterpriseControl(); control.Purchase = model.SelectedPurchaseForDetail;
            control.Purchases.Add(control.Purchase); control.Amount = 1m;
            await control.PaySupplierCommand.ExecuteAsync(null);
            Assert.Equal(4m, control.Purchase!.Financial!.Balance); Assert.Equal(0m, control.Amount);
            control.Quantity = 0.125m; control.Reason = "Devolución de prueba";
            await control.ReturnSupplierCommand.ExecuteAsync(null);
            Assert.Equal(0.125m, control.Purchase!.Items.Single().ReturnedQuantity);
            Assert.Equal(1.875m, control.PurchaseLine!.ReturnableQuantity); Assert.Equal(0m, control.Quantity);
            Assert.Contains("Devolución registrada", control.Summary);
            await model.LoadFinancialCommand.ExecuteAsync(null);
            Assert.Equal(0.125m, model.SelectedPurchaseForDetail!.Items.Single().ReturnedQuantity);
            Assert.Equal(control.Purchase.Financial.Balance, model.SelectedPurchaseForDetail.Financial!.Balance);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("close", false)]
    [InlineData("session", true)]
    [InlineData("search", false)]
    [InlineData("failure", true)]
    public async Task PurchaseFinanceUi_LateOrFailedReadCannotDisplayAFalseBalance(string action, bool fail)
    {
        string id = await ReceiveFinancialPurchaseAsync(paid: 1m);
        var service = PurchaseFinanceService();
        var document = (await service.GetByIdAsync(id))!;
        var delayed = new DelayedPurchaseFinancialHistory(service);
        var model = new PurchaseHistoryViewModel(delayed);
        await model.ViewPurchaseDetailCommand.ExecuteAsync(document);
        var loading = model.LoadFinancialCommand.ExecuteAsync(null);
        Assert.False(loading.IsCompleted);
        if (action == "close") model.ClosePurchaseDetailCommand.Execute(null);
        else if (action == "session") model.ClearSessionData();
        else if (action == "search") model.Search = "Otra compra";
        if (fail) delayed.Completion.SetException(new IOException("Synthetic failure"));
        else delayed.Completion.SetResult(await service.GetFinancialHistoryAsync(id));
        await loading;
        Assert.Empty(model.FinancialEntries); Assert.False(model.IsLoadingFinancial);
        if (action == "failure")
        {
            Assert.Null(model.SelectedPurchaseForDetail!.Financial);
            Assert.Equal("No calculado", model.SelectedPurchaseForDetail.BalanceDisplay);
            Assert.Contains("No significa", model.FinancialFeedback);
        }
        else Assert.Null(model.SelectedPurchaseForDetail);
    }

    private sealed class DelayedPurchaseFinancialHistory(IPurchaseService inner) : IPurchaseService
    {
        public TaskCompletionSource<PurchaseFinancialPage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PurchaseFinancialPage> GetFinancialHistoryAsync(string id, int page = 0, int size = 100) => Completion.Task;
        public Task<PurchaseDto?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<List<PurchaseDto>> GetPageAsync(int page, int pageSize = 100, string? search = null) => inner.GetPageAsync(page, pageSize, search);
        public Task<List<PurchaseDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto) => inner.RegisterPurchaseAsync(dto);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PurchaseDocumentUi_OpensVerifiedDetailAndNeverPrintsAnIncompleteOriginal(bool valid)
    {
        await SeedPurchaseDocumentAsync("UI-BUY", valid ? 1 : 1001);
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var printer = new ReceiptPrinter(true);
        var model = new PurchaseHistoryViewModel(service, printer);
        var view = new PurchaseHistoryView(); var window = Show(view, model);
        try
        {
            await model.LoadPurchasesAsync();
            var header = Assert.Single(model.Purchases);
            await model.ViewPurchaseDetailCommand.ExecuteAsync(header);
            Assert.True(model.IsPurchaseDetailDialogOpen);
            Assert.Equal(valid, model.SelectedPurchaseForDetail!.DetailsAvailable);
            await model.PrintPurchaseTicketCommand.ExecuteAsync(header);
            Assert.Equal(valid ? 1 : 0, printer.PurchaseTickets);
            if (!valid)
            {
                Assert.Contains("no verificable", model.FeedbackMessage);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("no verificable") == true);
            }
            else Assert.Contains("enviado al controlador", model.FeedbackMessage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PurchaseDocumentUi_DeferredDocumentCanLoadItsLinesForSupplierReturn()
    {
        await SeedPurchaseDocumentAsync("DEFERRED", 1);
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var actual = (await service.GetByIdAsync("DEFERRED"))!;
        var header = actual with { Items = [], DetailsAvailable = false, DetailWarning = PurchaseDocumentReader.Deferred };
        var model = EnterpriseControl(); model.Purchase = header;
        Assert.Empty(model.PurchaseLines);
        await model.LoadPurchaseDetailCommand.ExecuteAsync(null);
        Assert.True(model.Purchase!.DetailsAvailable); Assert.Single(model.PurchaseLines);
        Assert.NotNull(model.PurchaseLine);
        model.ClearDirectoryData();
        Assert.Null(model.Purchase); Assert.Null(model.PurchaseLine); Assert.Empty(model.PurchaseLines);
    }

    [AvaloniaTheory]
    [InlineData("detail", false)]
    [InlineData("detail", true)]
    [InlineData("print", false)]
    [InlineData("print", true)]
    public async Task PurchaseDocumentUi_LateDetailOrPrintReadCannotSurviveLogout(string action, bool fail)
    {
        await SeedPurchaseDocumentAsync("PRIVATE-UI", 1);
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var document = (await service.GetByIdAsync("PRIVATE-UI"))!;
        var delayed = new DelayedPurchaseDetail(service);
        var printer = new ReceiptPrinter(true);
        var model = new PurchaseHistoryViewModel(delayed, printer);
        model.Purchases.Add(document);
        var operation = action == "detail" ? model.ViewPurchaseDetailCommand.ExecuteAsync(document) : model.PrintPurchaseTicketCommand.ExecuteAsync(document);
        Assert.False(operation.IsCompleted);
        model.ClearSessionData();
        if (fail) delayed.Completion.SetException(new IOException("Synthetic read error"));
        else delayed.Completion.SetResult(document);
        await operation;
        Assert.Empty(model.Purchases); Assert.Null(model.SelectedPurchaseForDetail);
        Assert.False(model.IsPurchaseDetailDialogOpen); Assert.False(model.IsLoadingDetail);
        Assert.Empty(model.FeedbackMessage); Assert.Equal(0, printer.PurchaseTickets);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseDocumentUi_CloseOrSearchInvalidatesPendingDetail(bool search)
    {
        await SeedPurchaseDocumentAsync("FILTER-UI", 1);
        var service = new PurchaseService(new TestDbContextFactory(_options), _actor);
        var document = (await service.GetByIdAsync("FILTER-UI"))!;
        var delayed = new DelayedPurchaseDetail(service);
        var model = new PurchaseHistoryViewModel(delayed);
        var read = model.ViewPurchaseDetailCommand.ExecuteAsync(document);
        if (search) model.Search = "new filter"; else model.ClosePurchaseDetailCommand.Execute(null);
        delayed.Completion.SetResult(document);
        await read;
        Assert.False(model.IsPurchaseDetailDialogOpen); Assert.Null(model.SelectedPurchaseForDetail);
        Assert.False(model.IsLoadingDetail);
    }

    [Fact]
    public async Task PurchaseDocumentPrinter_RejectsIncompleteAndInvalidDocumentsBeforeReadingPrinterSettings()
    {
        var settings = new RejectPurchasePrintSettings();
        await using var driver = new EscPosPrinterService(settings);
        var invalid = new PurchaseDto("PRINT", "S", "Proveedor", "F", "", 3m, "", []);
        Assert.False(await driver.PrintPurchaseOrderAsync(invalid));
        Assert.False(await driver.PrintPurchaseOrderAsync(invalid with { DetailsAvailable = false }));
        Assert.False(settings.Called);
    }

    private sealed class RejectPurchasePrintSettings : ISettingsService
    {
        public bool Called;
        public Task<Ticketfy.Core.Models.Settings.AppSettings> GetAppSettingsAsync()
        { Called = true; throw new InvalidOperationException("No hardware configuration should be read"); }
        public Task<string?> GetAsync(string key) => throw new NotSupportedException();
        public Task SetAsync(string key, string value) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetAllAsync() => throw new NotSupportedException();
        public Task SaveAppSettingsAsync(Ticketfy.Core.Models.Settings.AppSettings settings) => throw new NotSupportedException();
    }

    private sealed class DelayedPurchaseDetail(IPurchaseService inner) : IPurchaseService
    {
        public TaskCompletionSource<PurchaseDto?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PurchaseDto?> GetByIdAsync(string id) => Completion.Task;
        public Task<List<PurchaseDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<List<PurchaseDto>> GetPageAsync(int page, int pageSize = 100, string? search = null) => inner.GetPageAsync(page, pageSize, search);
        public Task<PurchaseDto> RegisterPurchaseAsync(PurchaseDto dto) => inner.RegisterPurchaseAsync(dto);
    }
}
