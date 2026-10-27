using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Core.Services;
using Ticketfy.Data.Entities;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Settings_BusinessSaveFailureCannotLeavePartialIdentityOrJournal()
    {
        var service = new SettingsService(new TestDbContextFactory(_options), _actor);
        var original = await service.GetAppSettingsAsync();
        original.Company.CommercialName = "Negocio anterior";
        await service.SaveAppSettingsAsync(original);
        var before = await service.GetAllAsync();
        await using var db = Db();
        var journalBefore = await db.TransactionJournal.CountAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_business BEFORE INSERT ON settings
            WHEN NEW.key = 'EmpresaQrRedesUrl' BEGIN SELECT RAISE(ABORT, 'synthetic-business-failure'); END;
            """);
        var vm = new Ticketfy.ViewModels.Settings.EmpresaSettingsViewModel(service, loadOnCreate: false);
        await vm.LoadAsync();
        vm.EmpresaNombreComercial = "Negocio nuevo";
        vm.EmpresaEmailContacto = "lab@example.invalid";
        vm.EmpresaQrRedesUrl = "https://example.invalid";
        Assert.False(await vm.SaveAsync());
        Assert.NotEmpty(vm.FeedbackMessage);
        Assert.Equal(before.OrderBy(p => p.Key), (await service.GetAllAsync()).OrderBy(p => p.Key));
        Assert.Equal(journalBefore, await db.TransactionJournal.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_business;");
        Assert.True(await vm.SaveAsync());
        Assert.Equal("Negocio nuevo", (await service.GetAppSettingsAsync()).Company.CommercialName);
        Assert.True(await vm.SaveAsync());
        Assert.Equal(journalBefore + 1, await db.TransactionJournal.CountAsync());
    }

    [Fact]
    public async Task Settings_BusinessMetadataMergesCurrentSettingsAndRollsBackIfAuditFails()
    {
        var service = new SettingsService(new TestDbContextFactory(_options), _actor);
        var previous = await service.GetAppSettingsAsync();
        previous.System.AutoLockTimeoutMinutes = 3; previous.Hardware.PrinterPort = "SYNTHETIC";
        previous.Visual.PosCartWidth = 400; previous.Company.Email = "old@example.invalid";
        await service.SaveAppSettingsAsync(previous);
        var before = await service.GetAllAsync();
        await using var db = Db();
        var journalBefore = await db.TransactionJournal.CountAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_company_audit BEFORE INSERT ON transaction_journal
            WHEN NEW.event_type = 'BUSINESS_SETTINGS_UPDATED' BEGIN SELECT RAISE(ABORT, 'synthetic-business-audit'); END;
            """);
        var values = new Dictionary<string, string>
        {
            ["EmpresaNombreComercial"] = " Laboratorio ",
            ["EmpresaEmailContacto"] = "",
            ["EmpresaTelefonoFijo"] = "5544332211",
            ["EmpresaCalleYNumero"] = "Dirección sintética",
            ["EmpresaRazonSocial"] = "Razón de prueba",
            ["EmpresaRfc"] = "SYNTHETIC",
            ["EmpresaRegimenFiscal"] = "Régimen de prueba",
            ["EmpresaCodigoPostalFiscal"] = "00000",
            ["EmpresaSitioWeb"] = "https://example.invalid",
            ["EmpresaLogoPrincipalUrl"] = "synthetic.png",
            ["EmpresaThermalLogoThreshold"] = "128.5"
        };
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => service.SaveBusinessSettingsAsync(values));
        Assert.Contains("synthetic-business-audit", error.InnerException!.Message);
        Assert.Equal(before.OrderBy(p => p.Key), (await service.GetAllAsync()).OrderBy(p => p.Key));
        Assert.Equal(journalBefore, await db.TransactionJournal.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_company_audit;");
        await service.SaveBusinessSettingsAsync(values); await service.SaveBusinessSettingsAsync(values);
        var actual = await service.GetAppSettingsAsync();
        Assert.Equal("Laboratorio", actual.Company.CommercialName); Assert.Empty(actual.Company.Email);
        Assert.Equal("5544332211", actual.Company.Phone); Assert.Equal("Dirección sintética", actual.Company.Address);
        Assert.Equal("Razón de prueba", actual.Company.LegalName); Assert.Equal("SYNTHETIC", actual.Company.Rfc);
        Assert.Equal("Régimen de prueba", actual.Company.FiscalRegime); Assert.Equal("00000", actual.Company.ZipCode);
        Assert.Equal("https://example.invalid", actual.Company.Website); Assert.Equal("synthetic.png", actual.Company.LogoPath);
        Assert.Equal(3, actual.System.AutoLockTimeoutMinutes); Assert.Equal("SYNTHETIC", actual.Hardware.PrinterPort);
        Assert.Equal(400, actual.Visual.PosCartWidth); Assert.Equal(previous.Company.TimeZoneId, actual.Company.TimeZoneId);
        Assert.Equal("Laboratorio", await service.GetAsync("BusinessName")); Assert.Equal("", await service.GetAsync("BusinessEmail"));
        var journal = await db.TransactionJournal.SingleAsync(j => j.EventType == "BUSINESS_SETTINGS_UPDATED");
        Assert.Equal(CommercialTestSession.UserId.ToString(), journal.CashierUserId);
        Assert.DoesNotContain("Laboratorio", journal.Details);
        Assert.Equal(journalBefore + 1, await db.TransactionJournal.CountAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SettingsService(new TestDbContextFactory(_options)).SaveBusinessSettingsAsync(values));
        var user = await db.Users.SingleAsync(); user.IsActive = false; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveBusinessSettingsAsync(values));
    }

    [Theory]
    [InlineData("SystemGlobalAppSettings_V4", "{}")]
    [InlineData("FacturamaApiPassword", "synthetic")]
    [InlineData("EmpresaCertificadoKeyPath", "synthetic.key")]
    [InlineData("EmpresaNombreComercial", " ")]
    [InlineData("EmpresaNombreComercial", "bad\0name")]
    [InlineData("EmpresaTelefonoFijo", "123")]
    [InlineData("EmpresaWhatsappContacto", "ABCDEFGHIJ")]
    [InlineData("EmpresaEmailContacto", "not-an-email")]
    [InlineData("EmpresaThermalLogoThreshold", "NaN")]
    [InlineData("EmpresaThermalLogoThreshold", "256")]
    public async Task Settings_InvalidBusinessChangesDoNotWrite(string key, string value)
    {
        var service = new SettingsService(new TestDbContextFactory(_options), _actor);
        var before = await service.GetAllAsync();
        await using var db = Db(); var journalBefore = await db.TransactionJournal.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveBusinessSettingsAsync(new Dictionary<string, string> { [key] = value }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveBusinessSettingsAsync(new Dictionary<string, string>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveBusinessSettingsAsync(new Dictionary<string, string> { ["EmpresaNombreComercial"] = new string('a', 257) }));
        Assert.Equal(before.OrderBy(p => p.Key), (await service.GetAllAsync()).OrderBy(p => p.Key));
        Assert.Equal(journalBefore, await db.TransactionJournal.CountAsync());
    }

    [Fact]
    public async Task Settings_SectionSaveDoesNotOverwriteNewerSecurityOrBusinessData()
    {
        var service = new SettingsService(new TestDbContextFactory(_options), _actor);
        var oldVisual = await service.GetAppSettingsAsync();
        var security = await service.GetAppSettingsAsync();
        security.System.AutoLockTimeoutMinutes = 3;
        security.Company.CommercialName = "This section must not change company";
        await service.SaveSectionAsync(security, AppSettingsSection.Security);
        oldVisual.Visual.PosCartWidth = 420;
        await service.SaveSectionAsync(oldVisual, AppSettingsSection.Visual);
        var actual = await service.GetAppSettingsAsync();
        Assert.Equal(3, actual.System.AutoLockTimeoutMinutes);
        Assert.Equal(420, actual.Visual.PosCartWidth);
        Assert.NotEqual(security.Company.CommercialName, actual.Company.CommercialName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAsync("SystemGlobalAppSettings_V4", "{}"));
        actual.System.EnableAuditLogs = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAppSettingsAsync(actual));
        Assert.True((await service.GetAppSettingsAsync()).System.EnableAuditLogs);
    }

    [Fact]
    public async Task Settings_CustomBusinessRoleCannotAlterSecurityHardwareOrAudit()
    {
        await using var db = Db();
        var user = await db.Users.SingleAsync(); user.RoleString = "BUSINESS_EDITOR";
        db.Settings.Add(new SettingEntity
        {
            Key = "RolePermissions_BUSINESS_EDITOR",
            Value = JsonSerializer.Serialize(new Dictionary<string, bool>
            { [PermissionCatalog.SettingsBusiness] = true })
        });
        await db.SaveChangesAsync();
        var factory = new TestDbContextFactory(_options);
        var permissions = new UserRolePermissionEngine(new SettingsService(factory));
        var session = new SessionManager(); session.StartSession(permissions.MapToModel(user));
        var actor = new CommercialOperationContext(session, permissions, "TEST-REGISTER");
        var service = new SettingsService(factory, actor);
        var edited = await service.GetAppSettingsAsync();
        edited.System.EnablePinLock = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAppSettingsAsync(edited));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveSectionAsync(edited, AppSettingsSection.Security));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetAsync("PrinterComPort", "COM5"));
        edited.Hardware.PrinterPort = "COM5";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveSectionAsync(edited, AppSettingsSection.Hardware));
        // Saving the permitted visual section must ignore stale or malicious changes to other sections.
        edited.Visual.PosCartWidth = 400;
        await service.SaveSectionAsync(edited, AppSettingsSection.Visual);
        var actual = await service.GetAppSettingsAsync();
        Assert.True(actual.System.EnablePinLock);
        Assert.Equal("", actual.Hardware.PrinterPort);
        Assert.Equal(400, actual.Visual.PosCartWidth);
    }
}
