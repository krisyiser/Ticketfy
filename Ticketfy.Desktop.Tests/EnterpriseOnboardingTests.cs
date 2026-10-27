using Microsoft.EntityFrameworkCore;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.ViewModels;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task Onboarding_BusinessSaveRollsBackAllFieldsWhenJournalFails()
    {
        await using var db = Db();
        db.Settings.Add(new SettingEntity { Key = "InitialSetup.Stage", Value = "Business" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_setup BEFORE INSERT ON transaction_journal
            WHEN NEW.event_type IN ('APP_SETTINGS_UPDATED', 'INITIAL_BUSINESS_SAVED')
            BEGIN SELECT RAISE(ABORT, 'synthetic journal failure'); END;
            """);
        var settings = new SettingsService(new TestDbContextFactory(_options), _actor);
        int advances = 0;
        var vm = new SetupBusinessDataViewModel(settings, () => advances++)
        { BusinessName = "Tienda sintética", Phone = "5544332211", Email = "lab@example.invalid", Address = "Domicilio de prueba" };
        await vm.SaveAndContinueCommand.ExecuteAsync(null);
        Assert.Equal(0, advances);
        Assert.NotEmpty(vm.ErrorMessage);
        var saved = await settings.GetAllAsync();
        Assert.DoesNotContain("BusinessName", saved.Keys);
        Assert.DoesNotContain("EmpresaTelefonoFijo", saved.Keys);
        Assert.Equal("Business", saved["InitialSetup.Stage"]);
    }

    [Theory]
    [InlineData("abcdefghij")]
    [InlineData("１２３４５６７８９０")]
    public async Task Onboarding_RejectsTenNonAsciiPhoneCharacters(string phone)
    {
        var settings = new SettingsService(new TestDbContextFactory(_options), _actor);
        int advances = 0;
        var vm = new SetupBusinessDataViewModel(settings, () => advances++) { BusinessName = "Laboratorio", Phone = phone };
        await vm.SaveAndContinueCommand.ExecuteAsync(null);
        Assert.Equal(0, advances);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.DoesNotContain("BusinessName", (await settings.GetAllAsync()).Keys);
    }
}
