using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.State;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Navigation;
using Ticketfy.Views;
using Ticketfy.Views.Dialogs;
using Ticketfy.Views.Pos;
using Ticketfy.Views.History;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task FlowReview_PromotionStatusFailureKeepsRuleAndDoesNotExposeTechnicalDetails()
    {
        await SeedPromotionWorkspaceAsync(1);
        var options = new DbContextOptionsBuilder<Ticketfy.Data.AppDbContext>().UseSqlite(_connection)
            .AddInterceptors(new FailPromotionJournal()).Options;
        var model = new PromotionsViewModel(new PromotionService(new TestDbContextFactory(options), _actor));
        await model.InitializationTask;
        var rule = Assert.Single(model.Promotions);
        await model.ToggleStatusCommand.ExecuteAsync(rule);
        Assert.True(model.IsFeedbackError); Assert.Contains("Actualice", model.FeedbackMessage);
        Assert.DoesNotContain("Synthetic", model.FeedbackMessage); Assert.DoesNotContain("Exception", model.FeedbackMessage);
        await using var db = Db();
        Assert.Equal(1, (await db.Promotions.SingleAsync()).IsActive);
        Assert.Empty(await db.TransactionJournal.ToListAsync()); Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    [AvaloniaTheory]
    [InlineData("Efectivo")]
    [InlineData("Mixto")]
    public async Task FlowReview_ManualDiscountRecalculatesTenderChangeAndHistoricalTotal(string method)
    {
        var model = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), new EvidenceTerminal(this), UiCart(), 20m)
        { PaymentMethod = method };
        if (method == "Mixto") { model.CashAmount = 10m; model.CardAmount = 10m; }
        else model.ReceivedAmountInput = "20";
        var actor = new Ticketfy.Core.Models.UserModel { Id = CommercialTestSession.UserId, Role = Ticketfy.Core.Models.SystemRole.ADMIN };
        Assert.True(await model.ApplyManualDiscountAsync(10, "Prueba de descuento", currentUser: actor));
        Assert.Equal(18m, model.TotalToPay); Assert.Equal(18m, model.TotalBill); Assert.Equal(2m, model.ChangeAmount);
        await model.ConfirmPaymentCommand.ExecuteAsync(null);
        Assert.Empty(model.ErrorMessage);
        Assert.False(await model.ApplyManualDiscountAsync(10, "No modificar una venta confirmada", currentUser: actor));
        await using var db = Db(); var sale = await db.Sales.SingleAsync();
        Assert.Equal(18m, sale.Total); Assert.Equal(18m, (await db.SalePayments.ToListAsync()).Sum(p => p.Amount));
        Assert.Equal(method == "Mixto" ? 8m : 18m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        Assert.Equal(18m, (await Sales.GetByIdAsync(sale.Id))!.Total);
    }

    [AvaloniaTheory]
    [InlineData("es-MX")]
    [InlineData("es-ES")]
    public async Task FlowReview_CashSwitchRequiresReceiptAndSaleReconcilesWithHistory(string culture)
    {
        var previous = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var terminal = new EvidenceTerminal(this);
            var model = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m);
            var view = new CheckoutDialog(); var window = ReviewWindow(view, model, 1024, 500);
            try
            {
                model.PaymentMethod = "Tarjeta Débito/Crédito";
                Assert.True(model.ConfirmPaymentCommand.CanExecute(null));
                model.PaymentMethod = "Efectivo";
                Assert.Equal(0m, model.ReceivedAmount);
                Assert.False(model.ConfirmPaymentCommand.CanExecute(null));
                var input = view.FindControl<TextBox>("ReceivedAmountTextBox")!;
                input.Text = "20,50"; Dispatcher.UIThread.RunJobs();
                Assert.Equal(20.50m, model.ReceivedAmount); Assert.Equal(0.50m, model.ChangeAmount);
                ReviewClick(window, view.FindControl<Button>("ConfirmSaleButton")!);
                await model.ConfirmPaymentCommand.ExecutionTask!;
                await model.ConfirmPaymentCommand.ExecuteAsync(null);
                Assert.Empty(model.ErrorMessage); Assert.Equal(0, terminal.Calls);
                await using var db = Db();
                var sale = await db.Sales.SingleAsync();
                Assert.Equal(20m, sale.Total); Assert.Equal(0.50m, sale.ChangeAmount);
                Assert.Equal(20m, (await db.SalePayments.SingleAsync()).Amount);
                Assert.Equal(18d, (await db.Products.SingleAsync()).Stock);
                Assert.Equal(20m, (await db.Shifts.SingleAsync()).ExpectedBalance);
                var history = new HistoryViewModel(Sales, null!, loadOnCreate: false);
                await history.LoadSalesAsync();
                Assert.Equal(sale.Id, Assert.Single(history.Sales).Id);
                Assert.Equal(20m, history.TotalDayRevenue);
                history.Deactivate(); history.ClearSessionData();
            }
            finally { window.Close(); }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [AvaloniaTheory]
    [InlineData("20oops")]
    [InlineData("1,234.56")]
    [InlineData("20.001")]
    [InlineData("-20")]
    public async Task FlowReview_InvalidCashCannotCreateCheckoutOrMoney(string input)
    {
        var model = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), new EvidenceTerminal(this), UiCart(), 20m);
        model.ReceivedAmountInput = "30"; model.ReceivedAmountInput = input;
        Assert.False(model.ConfirmPaymentCommand.CanExecute(null));
        await model.ConfirmPaymentCommand.ExecuteAsync(null);
        await using var db = Db();
        Assert.Empty(await db.Sales.ToListAsync()); Assert.Empty(await db.CheckoutOperations.ToListAsync());
        Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
    }

    [AvaloniaFact]
    public async Task FlowReview_MixedInvalidVisibleFieldCannotChargePreviousAmount()
    {
        var terminal = new EvidenceTerminal(this);
        var model = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), terminal, UiCart(), 20m)
        { PaymentMethod = "Mixto", CashAmount = 10m, CardAmount = 10m };
        var view = new CheckoutDialog(); var window = ReviewWindow(view, model, 1024, 500);
        try
        {
            var field = view.FindControl<TextBox>("MixedCashInput")!;
            field.Text = "10oops"; Dispatcher.UIThread.RunJobs();
            Assert.True(DataValidationErrors.GetHasErrors(field));
            ReviewClick(window, view.FindControl<Button>("ConfirmSaleButton")!);
            Assert.Equal(0, terminal.Calls);
            await using var db = Db(); Assert.Empty(await db.Sales.ToListAsync());
            field.Text = "10"; Dispatcher.UIThread.RunJobs();
            Assert.False(DataValidationErrors.GetHasErrors(field), string.Join(";", DataValidationErrors.GetErrors(field) ?? []));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.IsEffectivelyVisible && DataValidationErrors.GetHasErrors(c));
            Assert.True(model.ConfirmPaymentCommand.CanExecute(null), model.ErrorMessage);
            ReviewClick(window, view.FindControl<Button>("ConfirmSaleButton")!);
            Assert.NotNull(model.ConfirmPaymentCommand.ExecutionTask);
            await model.ConfirmPaymentCommand.ExecutionTask!;
            Assert.Empty(model.ErrorMessage); Assert.Equal(1, terminal.Calls);
            Assert.Equal(2, await db.SalePayments.CountAsync());
            Assert.Equal(10m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FlowReview_CustomerInvalidCreditAndDuplicateCodeRequireCorrection()
    {
        var model = new CustomerDialogViewModel(CustomersService()) { Name = "  Cliente revisión  ", Email = "cliente@example.invalid", CustomerCode = "FLOW-CUSTOMER", CreditLimit = 50m };
        var view = new CustomerDialog(); var window = ReviewWindow(view, model, 800, 500);
        try
        {
            var field = view.FindControl<TextBox>("CustomerCreditInput")!;
            field.Text = "50oops"; Dispatcher.UIThread.RunJobs();
            Assert.True(DataValidationErrors.GetHasErrors(field));
            ReviewClick(window, view.FindControl<Button>("SaveCustomerButton")!);
            await using var db = Db(); Assert.Equal(1, await db.Customers.CountAsync());
            field.Text = "50,25"; Dispatcher.UIThread.RunJobs();
            ReviewClick(window, view.FindControl<Button>("SaveCustomerButton")!);
            await model.SaveCommand.ExecutionTask!;
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Empty(model.ErrorMessage);
            var saved = await db.Customers.SingleAsync(c => c.CustomerCode == "FLOW-CUSTOMER");
            Assert.Equal("Cliente revisión", saved.Name); Assert.Equal(50.25m, saved.CreditLimit); Assert.Equal("", saved.Phone);
            Assert.Single(await db.TransactionJournal.Where(j => j.EntityId == saved.Id).ToListAsync());
            var duplicate = new CustomerDialogViewModel(CustomersService()) { Name = "Otro", CustomerCode = "FLOW-CUSTOMER" };
            await duplicate.SaveCommand.ExecuteAsync(null);
            Assert.Contains("código", duplicate.ErrorMessage); Assert.True(duplicate.CanEditFields);
            duplicate.CustomerCode = "FLOW-OTHER";
            await duplicate.SaveCommand.ExecuteAsync(null);
            Assert.Empty(duplicate.ErrorMessage); Assert.Equal(3, await db.Customers.CountAsync());
            var edit = new CustomerDialogViewModel(CustomersService()); edit.LoadForEdit(model.SavedCustomer!);
            Assert.Equal("Editar cliente", edit.Title);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task FlowReview_CustomerServiceRejectsInvalidContactAndConflictingCreateIdentity()
    {
        var service = CustomersService();
        var valid = new CustomerDto("FLOW-SERVICE", "Cliente", "55 1234-5678", "correo@example.invalid", "", 25.50m, CustomerCode: "FLOW-SERVICE");
        foreach (var invalid in new[] { valid with { Nombre = "" }, valid with { Telefono = "55BAD12345678" }, valid with { Email = "not-an-email" }, valid with { LimiteCredito = -1m }, valid with { LimiteCredito = 5.001m } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(invalid));
        await service.AddAsync(valid); await service.AddAsync(valid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(valid with { Nombre = "Changed retry" }));
        await using var db = Db();
        Assert.Equal("5512345678", (await db.Customers.SingleAsync(c => c.Id == valid.Id)).Phone);
        Assert.Single(await db.TransactionJournal.Where(j => j.EntityId == valid.Id).ToListAsync());
    }

    [AvaloniaFact]
    public async Task FlowReview_PaymentLostResponseRetriesExactAmountAndStatementHasBothDocuments()
    {
        var saved = await Sales.SaveAsync(Sale("FLOW-CREDIT") with { CashAmount = 0m, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        var wrapper = new FlowCustomerService(CustomersService()) { LoseNextPaymentResponse = true };
        var model = new PaymentDialogViewModel(wrapper, "C", 20m) { Amount = 7.25m };
        var view = new PaymentDialog(); var window = ReviewWindow(view, model, 800, 430);
        int closes = 0; model.RequestClose += () => closes++;
        try
        {
            ReviewClick(window, view.FindControl<Button>("SaveCustomerPaymentButton")!);
            await model.SaveCommand.ExecutionTask!;
            Assert.True(model.IsPendingConfirmation); Assert.False(model.CanEditFields); Assert.Equal(0, closes);
            Assert.DoesNotContain("IOException", model.ErrorMessage);
            model.Amount = 1m; // Even an out-of-band property change cannot replace the recorded intent.
            ReviewClick(window, view.FindControl<Button>("SaveCustomerPaymentButton")!);
            await model.SaveCommand.ExecutionTask!; await model.SaveCommand.ExecuteAsync(null);
            Assert.Empty(model.ErrorMessage); Assert.Equal(1, closes);
            Assert.Equal(2, wrapper.Payments.Count); Assert.Same(wrapper.Payments[0], wrapper.Payments[1]);
            await using var db = Db();
            Assert.Equal(7.25m, (await db.CustomerPayments.SingleAsync()).Amount);
            Assert.Equal(7.25m, (await db.Shifts.SingleAsync()).ExpectedBalance);
            var statement = new CustomerStatementDialogViewModel(CustomersService(), "C", "Cliente", "", 999m);
            await statement.InitializationTask;
            Assert.Equal(12.75m, statement.CurrentDebt);
            Assert.Contains(statement.LedgerEntries, e => e.Concept == "Venta a crédito · " + saved.Id && e.Charge == 20m);
            Assert.Contains(statement.LedgerEntries, e => e.Concept == "Abono de cliente · " + saved.Id && e.Payment == 7.25m);
            var statementView = new CustomerStatementDialog(); var statementWindow = ReviewWindow(statementView, statement, 800, 450);
            try { VisibleButtonCenter(statementWindow, statementView.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "CERRAR ESTADO DE CUENTA")); }
            finally { statementWindow.Close(); statement.OnDismissed(); }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FlowReview_DismissedCustomerAndPaymentCannotCloseReplacement()
    {
        await Sales.SaveAsync(Sale("FLOW-DISMISS") with { CashAmount = 0m, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new FlowCustomerService(CustomersService()) { BeforePayment = completion.Task };
        var payment = new PaymentDialogViewModel(wrapper, "C", 20m) { Amount = 5m };
        var dialogs = new DialogCoordinator(); dialogs.ShowDialog(payment);
        int closes = 0; payment.RequestClose += () => { closes++; dialogs.TryCloseDialog(payment); };
        var work = payment.SaveCommand.ExecuteAsync(null);
        Assert.False(work.IsCompleted);
        var customer = new CustomerDialogViewModel(CustomersService()); dialogs.ShowDialog(customer);
        completion.SetResult(); await work;
        Assert.Equal(0, closes); Assert.Same(customer, dialogs.ActiveDialogViewModel);
        Assert.False(payment.CanSave); Assert.Empty(payment.ErrorMessage);
        dialogs.CloseDialog();
        Assert.False(customer.CanSave);
        await using var db = Db(); Assert.Single(await db.CustomerPayments.ToListAsync());
    }

    [AvaloniaFact]
    public async Task FlowReview_CustomerDirectoryClearsOldRowsRejectsStaleActionsAndRecovers()
    {
        var wrapper = new FlowCustomerService(CustomersService());
        var model = new CustomersViewModel(wrapper); await model.InitializationTask;
        var old = Assert.Single(model.Customers); int payments = 0;
        model.OpenAddPaymentRequested += _ => payments++;
        var pending = new TaskCompletionSource<CatalogPage<CustomerDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        wrapper.NextPage = pending.Task;
        var load = model.LoadCustomersAsync();
        Assert.Empty(model.Customers); Assert.True(model.IsLoading);
        model.OpenAddPaymentDialogCommand.Execute(old); Assert.Equal(0, payments);
        pending.SetException(new IOException("Synthetic read failure")); await load;
        Assert.True(model.IsFeedbackError); Assert.Contains("no disponible", model.PageDescription);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.False(model.IsFeedbackError); Assert.Empty(model.FeedbackMessage);
        model.OpenAddPaymentDialogCommand.Execute(old); Assert.Equal(0, payments);
        model.OpenAddPaymentDialogCommand.Execute(Assert.Single(model.Customers)); Assert.Equal(1, payments);
        model.ClearSessionData();
    }

    [AvaloniaTheory]
    [InlineData("es-MX")]
    [InlineData("es-ES")]
    public async Task FlowReview_PromotionEditorToCheckoutPreservesDiscountAuditAndHistory(string culture)
    {
        var previous = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var service = new PromotionService(new TestDbContextFactory(_options), _actor);
            var editor = new PromotionDialogViewModel(service, ProductsService()) { Name = "Oferta de revisión", AllProducts = true, DiscountValueText = "12,5" };
            await editor.CatalogLoadTask; Assert.False(editor.NeedsProductSearch);
            var editorView = new PromotionDialog(); var window = ReviewWindow(editorView, editor, 800, 500);
            try
            {
                ReviewClick(window, editorView.FindControl<Button>("SavePromotionButton")!);
                await editor.SaveCommand.ExecutionTask!; await editor.SaveCommand.ExecuteAsync(null);
                Assert.Empty(editor.ErrorMessage);
            }
            finally { window.Close(); }
            var promotion = Assert.Single(await service.GetAllAsync()); Assert.Equal(12.5m, promotion.DiscountValue);
            var cart = new CartStateStore { PromotionService = service }; cart.AddItem(UiCart()[0], 20);
            Assert.True(await cart.RefreshPromotionsAsync()); Assert.Equal(17.50m, cart.Total);
            var pos = new CartViewModel(cart, Sales, CustomersService(), loadOnCreate: false); await pos.LoadCustomersAsync();
            Assert.False(pos.NeedsCustomerSearch); int opened = 0; pos.OpenCheckoutRequested += () => opened++;
            var posView = new PosCartTicketView(); var posWindow = ReviewWindow(posView, pos, 500, 520);
            try { ReviewClick(posWindow, posView.FindControl<Button>("OpenCheckoutButton")!); await pos.CheckoutCommand.ExecutionTask!; Assert.Equal(1, opened); }
            finally { posWindow.Close(); }
            var checkout = new CheckoutDialogViewModel(Sales, CustomersService(), new TestPrintDispatcher(), new EvidenceTerminal(this), cart.Items.ToList(), cart.Total);
            checkout.SetExactCashCommand.Execute(null); await checkout.ConfirmPaymentCommand.ExecuteAsync(null);
            Assert.Empty(checkout.ErrorMessage);
            await service.SetActiveAsync(promotion.Id, false, "FLOW-DISABLE");
            Assert.True(await cart.RefreshPromotionsAsync()); Assert.Equal(20m, cart.Total);
            await using var db = Db(); var stored = await db.Sales.SingleAsync();
            var history = await Sales.GetByIdAsync(stored.Id);
            Assert.Equal(17.50m, history!.Total);
            Assert.Equal(promotion.Id, Assert.Single(history.Items).AppliedPromotionId);
            Assert.Equal(2.50m, history.Items.Single().AppliedDiscountAmount);
            Assert.Equal(2, await db.TransactionJournal.CountAsync(j => j.EntityId == promotion.Id));
            Assert.Equal(17.50m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [AvaloniaFact]
    public void FlowReview_NumericRecoveryDoesNotEraseInputOrSuppressRejectedValues()
    {
        var model = new FlowValidatedAmount();
        var field = new Ticketfy.Controls.NumericTextBox { AllowDecimals = true };
        field.Bind(TextBox.TextProperty, new Binding(nameof(model.Amount))
        { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var window = ReviewWindow(field, model, 500, 200);
        try
        {
            Assert.Equal("10", field.Text);
            field.SetCurrentValue(TextBox.TextProperty, "10oops"); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(BindingOperations.GetBindingExpressionBase(field, TextBox.TextProperty));
            Assert.True(DataValidationErrors.GetHasErrors(field)); Assert.Equal("10oops", field.Text); Assert.Equal(10m, model.Amount);
            field.SetCurrentValue(TextBox.TextProperty, "-1"); Dispatcher.UIThread.RunJobs();
            Assert.True(DataValidationErrors.GetHasErrors(field)); Assert.Equal("-1", field.Text); Assert.Equal(10m, model.Amount);
            field.SetCurrentValue(TextBox.TextProperty, "10"); Dispatcher.UIThread.RunJobs();
            Assert.False(DataValidationErrors.GetHasErrors(field)); Assert.Equal("10", field.Text); Assert.Equal(10m, model.Amount);
        }
        finally { window.Close(); }
    }

    public sealed class FlowValidatedAmount
    {
        private decimal _amount = 10m;
        public decimal Amount
        {
            get => _amount;
            set { if (value < 0) throw new InvalidOperationException("Positive only"); _amount = value; }
        }
    }

    [AvaloniaFact]
    public async Task FlowReview_CustomerAndHistoryButtonsRemainReachableInShortWindow()
    {
        var customers = new CustomersViewModel(CustomersService()); await customers.InitializationTask;
        int opened = 0; customers.OpenAddPaymentRequested += _ => opened++;
        customers.OpenStatementRequested += _ => opened++; customers.OpenEditCustomerRequested += _ => opened++;
        var view = new CustomersView(); var window = ReviewWindow(view, customers, 1024, 520);
        try
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Single(customers.Customers);
            var table = Assert.Single(view.GetVisualDescendants().OfType<DataGrid>());
            Assert.NotNull(table.Template);
            Assert.True(table.Bounds.Height > 40, $"Table bounds: {table.Bounds}");
            Assert.True(table.GetVisualDescendants().OfType<DataGridRow>().Any(), $"Rows missing; bounds: {table.Bounds}; source: {table.ItemsSource?.GetType().Name}; children: {string.Join(", ", table.GetVisualDescendants().Select(v => v.GetType().Name))}");
            foreach (var command in new System.Windows.Input.ICommand[] { customers.OpenAddPaymentDialogCommand, customers.OpenStatementDialogCommand, customers.OpenEditCustomerDialogCommand })
            {
                var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
                Assert.True(buttons.Any(b => ReferenceEquals(b.Command, command)), string.Join(" | ", buttons.Select(b => $"{b.Content}: {b.Command?.GetType().Name}, {b.DataContext?.GetType().Name}")));
                ReviewClick(window, buttons.Single(b => ReferenceEquals(b.Command, command)));
            }
            Assert.Equal(3, opened);
        }
        finally { window.Close(); customers.ClearSessionData(); }
        await Sales.SaveAsync(Sale("FLOW-HISTORY-BUTTONS"));
        var history = new HistoryViewModel(Sales, null!, loadOnCreate: false); await history.LoadSalesAsync();
        var historyView = new HistorySalesTableView(); window = ReviewWindow(historyView, history, 1024, 520);
        try
        {
            foreach (var command in new System.Windows.Input.ICommand[] { history.ViewSaleDetailCommand, history.OpenReturnDialogCommand, history.RePrintTicketCommand, history.CancelSaleCommand })
                VisibleButtonCenter(window, historyView.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, command)));
            var detail = historyView.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, history.ViewSaleDetailCommand));
            ReviewClick(window, detail); await history.ViewSaleDetailCommand.ExecutionTask!;
            Assert.True(history.IsSaleDetailDialogOpen); Assert.Equal(20m, history.SelectedSaleForDetail!.Total);
        }
        finally { window.Close(); history.ClearSessionData(); }
    }

    [AvaloniaFact]
    public async Task FlowReview_PromotionBlankQuantityAndMalformedValueCannotSaveOldRule()
    {
        var service = new PromotionService(new TestDbContextFactory(_options), _actor);
        var model = new PromotionDialogViewModel(service) { Name = "Revisar cantidad", AllProducts = true, DiscountValueText = "1,234.56" };
        await model.SaveCommand.ExecuteAsync(null); Assert.NotEmpty(model.ErrorMessage);
        model.DiscountValueText = "10";
        var view = new PromotionDialog(); var window = ReviewWindow(view, model, 800, 500);
        try
        {
            var quantity = view.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Minimum == 0.001m);
            quantity.Value = null; Assert.Null(model.MinimumQuantity);
            ReviewClick(window, view.FindControl<Button>("SavePromotionButton")!); await model.SaveCommand.ExecutionTask!;
            Assert.NotEmpty(model.ErrorMessage); Assert.False(model.IsPendingConfirmation);
            await using var db = Db(); Assert.Empty(await db.Promotions.ToListAsync());
            quantity.Value = 1m;
            ReviewClick(window, view.FindControl<Button>("SavePromotionButton")!); await model.SaveCommand.ExecutionTask!;
            Assert.Empty(model.ErrorMessage); Assert.Single(await db.Promotions.ToListAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FlowReview_PaymentInvalidInputAndCloseFailureCannotDuplicateCash()
    {
        await Sales.SaveAsync(Sale("FLOW-ABONO-INPUT") with { CashAmount = 0m, IsCredit = true, CustomerId = "C", PaymentMethod = "Crédito" });
        var model = new PaymentDialogViewModel(CustomersService(), "C", 20m) { Amount = 5m };
        model.RequestClose += () => throw new IOException("Synthetic screen failure");
        var view = new PaymentDialog(); var window = ReviewWindow(view, model, 800, 430);
        try
        {
            var field = Assert.Single(view.GetVisualDescendants().OfType<Ticketfy.Controls.NumericTextBox>());
            field.Text = "5oops"; Dispatcher.UIThread.RunJobs();
            ReviewClick(window, view.FindControl<Button>("SaveCustomerPaymentButton")!);
            await using var db = Db(); Assert.Empty(await db.CustomerPayments.ToListAsync());
            field.Text = "5"; Dispatcher.UIThread.RunJobs();
            Assert.False(DataValidationErrors.GetHasErrors(field));
            ReviewClick(window, view.FindControl<Button>("SaveCustomerPaymentButton")!); await model.SaveCommand.ExecutionTask!;
            Assert.Contains("Abono registrado", model.ErrorMessage); Assert.False(model.CanSave);
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Equal(5m, (await db.CustomerPayments.SingleAsync()).Amount);
            Assert.Equal(15m, (await CustomersService().GetStatementAsync("C")).Balance);
            Assert.Equal(5m, (await db.Shifts.SingleAsync()).ExpectedBalance);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FlowReview_CustomerLostCreateResponseKeepsOriginalProfileAndOneHistoryEntry()
    {
        var wrapper = new FlowCustomerService(CustomersService()) { LoseNextCreateResponse = true };
        var model = new CustomerDialogViewModel(wrapper) { Name = "Perfil original", CustomerCode = "FLOW-CREATE" };
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.IsPendingConfirmation); Assert.False(model.CanEditFields);
        model.Name = "Cambio fuera de pantalla";
        await model.SaveCommand.ExecuteAsync(null); await model.SaveCommand.ExecuteAsync(null);
        Assert.Empty(model.ErrorMessage); Assert.Equal("Perfil original", model.SavedCustomer!.Name);
        await using var db = Db();
        var saved = await db.Customers.SingleAsync(c => c.CustomerCode == "FLOW-CREATE");
        Assert.Equal("Perfil original", saved.Name);
        Assert.Single(await db.TransactionJournal.Where(j => j.EntityId == saved.Id).ToListAsync());
    }

    private sealed class FlowCustomerService(ICustomerService inner) : ICustomerService
    {
        public bool LoseNextCreateResponse { get; set; }
        public bool LoseNextPaymentResponse { get; set; }
        public Task? BeforePayment { get; set; }
        public Task<CatalogPage<CustomerDto>>? NextPage { get; set; }
        public List<CustomerPaymentDto> Payments { get; } = [];
        public Task<List<CustomerDto>> GetAllAsync() => inner.GetAllAsync();
        public Task<CustomerDto?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<CatalogPage<CustomerDto>> GetPageAsync(int page, int size = 100, string? search = null)
        { var next = NextPage; NextPage = null; return next ?? inner.GetPageAsync(page, size, search); }
        public async Task AddAsync(CustomerDto dto)
        {
            await inner.AddAsync(dto);
            if (LoseNextCreateResponse) { LoseNextCreateResponse = false; throw new IOException("Synthetic lost creation response"); }
        }
        public Task UpdateAsync(CustomerDto dto) => inner.UpdateAsync(dto);
        public Task UpdateDebtAsync(string id, decimal debt) => inner.UpdateDebtAsync(id, debt);
        public async Task AddPaymentAsync(CustomerPaymentDto payment)
        {
            Payments.Add(payment); if (BeforePayment is not null) await BeforePayment;
            await inner.AddPaymentAsync(payment);
            if (LoseNextPaymentResponse) { LoseNextPaymentResponse = false; throw new IOException("Synthetic lost response"); }
        }
        public Task<bool> RegisterCustomerPaymentAsync(string id, decimal amount, string method, string notes, string? operationId = null) => inner.RegisterCustomerPaymentAsync(id, amount, method, notes, operationId);
        public Task<List<CustomerPaymentDto>> GetPaymentsAsync(string id) => inner.GetPaymentsAsync(id);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task UpdatePointsAsync(string id, double points) => inner.UpdatePointsAsync(id, points);
        public Task<FiscalClientDto?> GetFiscalDataAsync(string id) => inner.GetFiscalDataAsync(id);
        public Task SaveFiscalDataAsync(FiscalClientDto data) => inner.SaveFiscalDataAsync(data);
    }
}
