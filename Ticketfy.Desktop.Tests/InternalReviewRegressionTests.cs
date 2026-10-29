using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Controls;
using Ticketfy.Converters;
using Ticketfy.Core.Helpers;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Dialogs;
using Ticketfy.ViewModels.Suppliers;
using Ticketfy.Views;
using Ticketfy.Views.Dialogs;
using Ticketfy.Views.Expenses;
using Ticketfy.Views.Suppliers;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    // Test the installed theme and a short usable viewport, not only command bindings at 1200x900.
    private static Window ReviewWindow(Control view, object model, int width, int height)
    {
        view.DataContext = model;
        var window = new Window { Content = view, Width = width, Height = height };
        window.Styles.Add(new Semi.Avalonia.SemiTheme());
        window.Styles.Add(new StyleInclude(new Uri("avares://Ticketfy.Desktop/")) { Source = new Uri("avares://Ticketfy.Desktop/Styles/AppTheme.axaml") });
        window.Resources["TextSecondaryBrush"] = Brushes.DimGray;
        window.Resources["AccentWarningBrush"] = Brushes.DarkOrange;
        window.Resources["AppBaseFontSize"] = 14d;
        window.Resources["ControlMinHeight"] = 38d;
        window.Resources["ControlPadding"] = new Thickness(14, 10);
        window.Resources["TransitionDuration"] = TimeSpan.Zero;
        window.Resources["HoverBgBrush"] = Brushes.LightGray;
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window;
    }

    private static Point VisibleButtonCenter(Window window, Button button)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Assert.True(button.IsEffectivelyVisible); Assert.True(button.IsEffectivelyEnabled);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Assert.InRange(point.X, 1, window.Bounds.Width - 1);
        Assert.InRange(point.Y + button.Bounds.Height / 2, button.Bounds.Height, window.Bounds.Height);
        // An element arranged beyond a clipped parent is not a clickable button.
        var hit = window.InputHitTest(point) as Visual;
        Assert.NotNull(hit);
        Assert.True(ReferenceEquals(hit, button) || hit!.GetVisualAncestors().Contains(button));
        return point;
    }

    private static void ReviewClick(Window window, Button button)
    {
        var point = VisibleButtonCenter(window, button);
        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        point = VisibleButtonCenter(window, button);
        window.MouseDown(point, MouseButton.Left);
        Assert.True(button.IsPressed, $"Pointer did not press {button.Name} at {point}; hovered: {button.IsPointerOver}");
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(1280, 630)]
    [InlineData(1024, 520)]
    public async Task Review_ExpenseCommitIsVisibleAndActuallyRegistersAtShortWindowSizes(int width, int height)
    {
        await Sales.SaveAsync(Sale("REVIEW-FLOAT"));
        var factory = new TestDbContextFactory(_options);
        var model = new ExpensesViewModel(new ExpenseService(factory, _actor), shiftService: new ShiftService(factory, _actor));
        await model.InitializationTask;
        var view = new ExpensesView();
        var window = ReviewWindow(view, model, width, height);
        try
        {
            model.ExpenseAmount = 5.25m; model.ExpenseDescription = "Material de prueba";
            var button = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, model.AddExpenseCommand));
            var point = VisibleButtonCenter(window, button);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.IsEffectivelyVisible && DataValidationErrors.GetHasErrors(c));
            ReviewClick(window, button);
            Assert.NotNull(model.AddExpenseCommand.ExecutionTask);
            if (model.AddExpenseCommand.ExecutionTask is { } pending) await pending;
            await using var db = Db();
            Assert.Equal(5.25m, (await db.Expenses.SingleAsync()).Amount);
            Assert.Equal(14.75m, (await db.Shifts.SingleAsync()).ExpectedBalance);
            Assert.Single(model.Expenses);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Crédito", 0)]
    [InlineData("Efectivo", 6)]
    [InlineData("Transferencia", 6)]
    public async Task Review_PurchaseSelectionsStaySynchronizedAndCommitStockPaymentAndSupplierDebt(string method, int paid)
    {
        var factory = new TestDbContextFactory(_options);
        var service = new PurchaseService(factory, _actor);
        await Sales.SaveAsync(Sale("REVIEW-PURCHASE-FLOAT"));
        var model = new PurchaseOrderViewModel(service, ProductsService(), new SupplierService(factory, _actor));
        await model.LoadCatalogAsync();
        var view = new PurchaseOrderView(); var window = ReviewWindow(view, model, 1024, 500);
        try
        {
            var supplier = view.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, model.Suppliers));
            var product = view.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, model.AvailableProducts));
            supplier.SelectedItem = Assert.Single(model.Suppliers);
            await model.LoadProductsAsync();
            product.SelectedItem = Assert.Single(model.AvailableProducts);
            Assert.Equal("P", model.SelectedProduct!.Id);
            Assert.Equal(3m.ToString("F2"), model.PurchaseUnitPrice);
            model.PurchaseQuantity = "2";
            model.AddItemToPurchaseDraftCommand.Execute(null);
            model.PurchasePaymentMethod = method;
            Assert.Equal((decimal)paid, model.PurchasePaidAmount);
            model.PurchasePaymentReference = method == "Transferencia" ? "PRUEBA-BANCO-001" : "";
            var button = view.FindControl<Button>("ConfirmPurchaseBtn")!;
            var point = VisibleButtonCenter(window, button);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.IsEffectivelyVisible && DataValidationErrors.GetHasErrors(c));
            ReviewClick(window, button);
            Assert.NotNull(model.ConfirmPurchaseCommand.ExecutionTask);
            await model.ConfirmPurchaseCommand.ExecutionTask!;
            Assert.False(model.IsFeedbackError, model.FeedbackMessage);
            Assert.Null(supplier.SelectedItem); Assert.Null(product.SelectedItem);
            Assert.Empty(model.DraftPurchaseItems);
            await model.ConfirmPurchaseCommand.ExecuteAsync(null);
            await using var db = Db();
            var purchase = await db.Purchases.SingleAsync();
            Assert.Equal(20d, (await db.Products.SingleAsync()).Stock);
            Assert.Equal(6m, purchase.TotalCost);
            Assert.Equal(paid == 0 ? 0 : 1, await db.SupplierPayments.CountAsync());
            Assert.Equal(6m - paid, (await service.GetByIdAsync(purchase.Id))!.Financial!.Balance);
            Assert.Single(await db.InventoryMovements.Where(m => m.DocumentId == purchase.Id).ToListAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Review_EmptyPaidAmountMustBeCorrectedBeforeReceivingThePurchase()
    {
        await Sales.SaveAsync(Sale("REVIEW-EMPTY-FLOAT"));
        var factory = new TestDbContextFactory(_options);
        var model = new PurchaseOrderViewModel(new PurchaseService(factory, _actor), ProductsService(), new SupplierService(factory, _actor));
        await model.LoadCatalogAsync();
        model.SelectedSupplier = Assert.Single(model.Suppliers);
        await model.LoadProductsAsync();
        model.SelectedProduct = Assert.Single(model.AvailableProducts);
        model.AddItemToPurchaseDraftCommand.Execute(null);
        model.PurchasePaymentMethod = "Efectivo";
        var view = new PurchaseOrderView(); var window = ReviewWindow(view, model, 1024, 500);
        try
        {
            var field = view.GetVisualDescendants().OfType<NumericUpDown>().Single();
            field.Value = null;
            Assert.Null(model.PurchasePaidAmount);
            await model.ConfirmPurchaseCommand.ExecuteAsync(null);
            Assert.True(model.IsFeedbackError); Assert.Contains("cuánto pagó", model.FeedbackMessage);
            await using var db = Db(); Assert.Empty(await db.Purchases.ToListAsync());
            field.Value = 2m;
            await model.ConfirmPurchaseCommand.ExecuteAsync(null);
            Assert.False(model.IsFeedbackError, model.FeedbackMessage);
            Assert.Equal(2m, (await db.SupplierPayments.SingleAsync()).Amount);
            Assert.Single(await db.Purchases.ToListAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Review_ProductFormKeepsOnlyRequestedInventoryFieldsAndPreservesHiddenMetadata()
    {
        await using var db = Db();
        var entity = await db.Products.SingleAsync();
        entity.SatProductCode = "01010101"; entity.SatUnitCode = "H87"; entity.TaxRate = 0.08m; entity.MaxStock = 200;
        await db.SaveChangesAsync();
        var service = ProductsService();
        var model = new ProductDialogViewModel(service);
        model.LoadProductForEdit((await service.GetByIdAsync("P"))!);
        await model.LoadCategoriesAndSuppliersAsync();
        var view = new ProductDialog(); var window = ReviewWindow(view, model, 700, 500);
        try
        {
            Assert.Empty(view.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("SAT") == true);
            Assert.False(model.NeedsSupplierSearch); Assert.False(model.NeedsStockAdjustmentReason);
            model.Name = "Producto corregido";
            VisibleButtonCenter(window, view.FindControl<Button>("SaveProductButton")!);
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Empty(model.ErrorMessage);
            var saved = (await service.GetByIdAsync("P"))!;
            Assert.Equal("01010101", saved.SatProductCode); Assert.Equal("H87", saved.SatUnitCode);
            Assert.Equal(0.08m, saved.TaxRate); Assert.Equal(200, saved.MaxStock);
            Assert.Equal(20, saved.Stock);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Review_InvalidNumericPasteCannotSubmitPreviousAmountAndDoesNotExposeException()
    {
        await Sales.SaveAsync(Sale("REVIEW-INVALID-FLOAT"));
        var model = new ExpensesViewModel(new ExpenseService(new TestDbContextFactory(_options), _actor));
        await model.InitializationTask;
        model.ExpenseAmount = 5m; model.ExpenseDescription = "Prueba de importe inválido";
        var view = new ExpenseEntryView(); var window = ReviewWindow(view, model, 1100, 650);
        try
        {
            var field = view.GetVisualDescendants().OfType<NumericTextBox>().Single();
            field.Text = "5oops0";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("5oops0", field.Text);
            Assert.True(DataValidationErrors.GetHasErrors(field));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Exception") == true);
            var button = view.FindControl<Button>("RegisterExpenseButton")!;
            var point = VisibleButtonCenter(window, button);
            ReviewClick(window, button);
            if (model.AddExpenseCommand.ExecutionTask is { } pending) await pending;
            await using var db = Db(); Assert.Empty(await db.Expenses.ToListAsync());
            field.Text = "5.25"; Dispatcher.UIThread.RunJobs();
            Assert.False(DataValidationErrors.GetHasErrors(field));
            Assert.Equal(5.25m, model.ExpenseAmount);
            ReviewClick(window, button);
            Assert.NotNull(model.AddExpenseCommand.ExecutionTask);
            await model.AddExpenseCommand.ExecutionTask;
            Assert.Equal(5.25m, (await db.Expenses.SingleAsync()).Amount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Review_IncomeAndWithdrawalPersistOnceWithoutBecomingOperatingExpenses()
    {
        var factory = new TestDbContextFactory(_options);
        var shifts = new ShiftService(factory, _actor);
        var model = new ExpensesViewModel(new ExpenseService(factory, _actor), shiftService: shifts);
        await model.InitializationTask;
        var view = new ExpenseEntryView(); var window = ReviewWindow(view, model, 1100, 600);
        try
        {
            model.EntryKind = "Ingreso de efectivo"; model.ExpenseAmount = 100m; model.ExpenseDescription = "Cambio para caja";
            await ClickTwiceAsync(view, model.AddExpenseCommand);
            model.EntryKind = "Retiro de efectivo"; model.ExpenseAmount = 25m; model.ExpenseDescription = "Resguardo";
            await ClickTwiceAsync(view, model.AddExpenseCommand);
            model.SelectedHistory = "Ingresos y retiros del turno";
            await model.LoadExpensesAsync();
            Assert.Equal(2, model.CashMovements.Count);
            await using var db = Db();
            Assert.Empty(await db.Expenses.ToListAsync());
            Assert.Equal(75m, (await db.Shifts.SingleAsync()).ExpectedBalance);
            Assert.Equal(2, await db.TransactionJournal.CountAsync(j => j.EventType == "CASH_MOVEMENT"));
            Assert.All(model.CashMovements, m => { Assert.Equal("SHIFT-1", m.ShiftId); Assert.Equal("TEST-REGISTER", m.RegisterId); });
            model.ClearSessionData(); Assert.Empty(model.CashMovements);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Review_ManualMovementPagesAreBoundedAndRequireOwnShiftAndPermission()
    {
        var service = new ShiftService(new TestDbContextFactory(_options), _actor);
        for (int i = 0; i < 4; i++) await service.RegisterMovementAsync(1m, false, "Prueba", $"MOV-{i}");
        var page = await service.GetManualMovementPageAsync(0, 2);
        Assert.Equal(4, page.TotalCount); Assert.True(page.HasMore);
        var second = await service.GetManualMovementPageAsync(1, 2);
        Assert.False(second.HasMore);
        Assert.Equal(4, page.Items.Concat(second.Items).Select(r => r.Id).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetManualMovementPageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetManualMovementPageAsync(0, 101));
        await using var db = Db();
        (await db.Shifts.SingleAsync()).CashierUserId = Guid.NewGuid().ToString(); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetManualMovementPageAsync(0));
        (await db.Users.SingleAsync()).IsActive = false; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetManualMovementPageAsync(0));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("R7m!a", true)]
    [InlineData("123456", true)]
    [InlineData("R7m!a9", false)]
    [InlineData("Ár7!o9", false)]
    public void Review_PasswordMinimumIsSixAndCommonSecretsRemainRejected(string secret, bool rejected)
    {
        Assert.Equal(6, AccountInputPolicy.PasswordMinimumCodePoints);
        Assert.Equal(rejected, PasswordPolicy.RejectionReason(secret, "operador") is not null);
    }

    [Theory]
    [InlineData("es-MX", "123.45")]
    [InlineData("es-ES", "123,45")]
    public void Review_NumericConversionPreservesDecimalTypesAndRejectsMalformedInput(string culture, string text)
    {
        var converter = StringToDoubleConverter.Instance; var locale = CultureInfo.GetCultureInfo(culture);
        Assert.Equal(123.45m, Assert.IsType<decimal>(converter.ConvertBack(text, typeof(decimal?), null, locale)));
        Assert.Null(converter.ConvertBack("", typeof(decimal?), null, locale));
        Assert.Equal(0m, converter.ConvertBack("", typeof(decimal), null, locale));
        Assert.Equal(123.45d, converter.ConvertBack(text, typeof(double?), null, locale));
        Assert.Throws<FormatException>(() => converter.ConvertBack("1e3", typeof(decimal), null, locale));
        Assert.Throws<FormatException>(() => converter.ConvertBack("5oops0", typeof(decimal), null, locale));
    }

    [Fact]
    public void Review_TechnicalErrorsAreNotOperatorMessages()
    {
        var message = UserFacingErrors.Message(new InvalidOperationException("SELECT password FROM Users"));
        Assert.DoesNotContain("SELECT", message);
        Assert.DoesNotContain("Exception", UserFacingErrors.ValidationConverter(new InvalidCastException()).ToString());
    }
}
