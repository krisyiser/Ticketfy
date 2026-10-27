using Avalonia.Headless.XUnit;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Services;
using Ticketfy.Services.Auth;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;
using Ticketfy.ViewModels.Navigation;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [AvaloniaFact]
    public async Task DeferredPos_LoadsOnEntryAndCashierSwitchButStaysEmptyDuringInitialSetupAndLogout()
    {
        await using var db = Db();
        var factory = new TestDbContextFactory(_options);
        var session = new SessionManager();
        var permissions = new UserRolePermissionEngine();
        var actor = new CommercialOperationContext(session, permissions, "TEST-REGISTER");
        var user = permissions.MapToModel(await db.Users.SingleAsync());
        var pos = new PosViewModel(new ProductService(factory, actor), new ShiftNoteService(factory, actor),
            new CustomerService(factory, actor), session, loadOnCreate: false);
        var login = new LoginViewModel(new AuthService(new UserService(factory, operations: actor)), session, null!);
        var settings = new SettingsViewModel(new UserService(factory, operations: actor), new SettingsService(factory, actor));
        var navigation = new NavigationService(login, pos, null!, null!, null!, null!, null!, settings, null!, null!, null!, actor, factory, session);
        session.StartSession(user);
        Assert.True(navigation.IsAtLogin);
        Assert.Empty(pos.Cart.Customers); Assert.Empty(pos.Catalog.Products);
        navigation.GoToPos(); await pos.LoadSessionAsync();
        Assert.Contains(pos.Cart.Customers, c => c.Id == "C"); Assert.Single(pos.Catalog.Products);
        navigation.GoTo(settings); await settings.CloseCommand.ExecuteAsync(null);
        Assert.True(navigation.IsAtPos);
        session.SwitchCashier(user); await navigation.SessionActivationTask;
        Assert.True(navigation.IsAtPos);
        Assert.Contains(pos.Cart.Customers, c => c.Id == "C"); Assert.Single(pos.Catalog.Products);
        var disabled = await db.Users.SingleAsync(); disabled.IsActive = false; await db.SaveChangesAsync();
        navigation.GoTo(settings); await settings.CloseCommand.ExecuteAsync(null);
        Assert.Same(settings, navigation.ActiveViewModel); Assert.NotEmpty(navigation.AuthorizationError);
        navigation.GoToLogin(); await navigation.SessionActivationTask;
        Assert.True(navigation.IsAtLogin); Assert.Empty(pos.Cart.Customers); Assert.Empty(pos.Catalog.Products);
    }
}
