using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Core.State;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.Views;
using Ticketfy.Views.Pos;

namespace Ticketfy.Desktop.Tests;

public sealed class Stage4UiFlowTests
{
    [AvaloniaFact]
    public async Task LoginView_PropagatesInputAndDisplaysValidationError()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var viewModel = new LoginViewModel(
            new AuthService(new UserService(context)),
            new SessionManager(),
            new NoOpDialogService());
        var view = new LoginView { DataContext = viewModel };
        var window = new Window { Content = view };
        window.Show();

        var textBoxes = view.GetVisualDescendants().OfType<TextBox>().ToArray();
        Assert.Equal(2, textBoxes.Length);
        textBoxes[0].Text = "cajero";
        textBoxes[1].Text = string.Empty;
        var button = Assert.Single(view.GetVisualDescendants().OfType<Button>());
        button.Command!.Execute(button.CommandParameter);
        await viewModel.LoginCommand.ExecutionTask!;
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal("cajero", viewModel.Username);
        Assert.Equal("Usuario y contraseña requeridos.", viewModel.ErrorMessage);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Usuario y contraseña requeridos." && text.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PosCartView_RendersTicketAndRaisesCheckoutFromPrimaryButton()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var cart = new CartStateStore();
        var viewModel = new CartViewModel(
            cart,
            new SaleService(new TestDbContextFactory(options), new CoOccurrenceQueue(), new SpyAuditService()),
            new CustomerService(context));
        cart.AddItem(new CartItemDto("P-UI", "Producto UI", 19.90m, 2), absoluteDbStock: 10);
        bool checkoutRequested = false;
        viewModel.OpenCheckoutRequested += () => checkoutRequested = true;
        var view = new PosCartTicketView { DataContext = viewModel };
        var window = new Window { Content = view };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var checkout = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == "Efectivo");
        checkout.Command!.Execute(checkout.CommandParameter);

        Assert.True(checkoutRequested);
        Assert.Equal("Efectivo", viewModel.InitialPaymentMode);
        Assert.Equal(39.80m, cart.Total);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Producto UI");
        window.Close();
    }

    private sealed class NoOpDialogService : IDialogService
    {
        public Task<TResult?> ShowModalAsync<TViewModel, TResult>(TViewModel viewModel) where TViewModel : class => Task.FromResult(default(TResult));
        public Task ShowLockScreenAsync() => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public void CloseModal() { }
    }
}
