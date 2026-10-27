using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

public partial class SettingsService
{
    public async Task<InitialSetupStage> GetInitialSetupStageAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        return await InitialSetupStateStore.ReadAsync(db);
    }

    private async Task<OperationIdentity> RequireSetupAdministratorAsync(AppDbContext db)
    {
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Inicie sesión como administrador para completar la configuración."))
            .RequireAsync(db, PermissionCatalog.SettingsBusiness, false);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(actor.UserId));
        if (UserAccountPolicy.EffectiveRole(user) != "ADMINISTRADOR")
            throw new UnauthorizedAccessException("La configuración inicial requiere una cuenta administradora.");
        return actor;
    }

    public async Task<InitialBusinessProfile> GetInitialBusinessAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await RequireSetupAdministratorAsync(db);
        var company = (await ReadAppSettingsAsync(db)).Company;
        return new(company.CommercialName, company.Phone, company.Email, company.Address);
    }

    public async Task SaveInitialBusinessAsync(InitialBusinessProfile profile)
    {
        var validated = profile.Validated();
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireSetupAdministratorAsync(db);
        var stage = await InitialSetupStateStore.ReadAsync(db);
        if (stage is not (InitialSetupStage.Business or InitialSetupStage.Staff))
            throw new InvalidOperationException("La configuración inicial no está pendiente. Use Configuración del negocio para editarla.");
        var settings = await ReadAppSettingsAsync(db);
        var current = settings.Company;
        if (stage == InitialSetupStage.Staff && validated == new InitialBusinessProfile(current.CommercialName, current.Phone, current.Email, current.Address)) return;
        current.CommercialName = validated.Name; current.Phone = validated.Phone;
        current.Email = validated.Email; current.Address = validated.Address;
        var values = new Dictionary<string, string>
        {
            [AppSettingsJsonKey] = JsonSerializer.Serialize(settings),
            ["BusinessName"] = validated.Name,
            ["EmpresaNombreComercial"] = validated.Name,
            ["BusinessPhone"] = validated.Phone,
            ["EmpresaTelefonoFijo"] = validated.Phone,
            ["BusinessEmail"] = validated.Email,
            ["EmpresaEmailContacto"] = validated.Email,
            ["BusinessAddress"] = validated.Address,
            ["EmpresaCalleYNumero"] = validated.Address,
            [InitialSetupStateStore.Key] = nameof(InitialSetupStage.Staff)
        };
        foreach (var pair in values) await SetValueAsync(db, pair.Key, pair.Value);
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "INITIAL_BUSINESS_SAVED", InitialSetupStateStore.Key, 0, "Datos iniciales del negocio guardados; valores omitidos");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task CompleteInitialSetupAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireSetupAdministratorAsync(db);
        var stage = await InitialSetupStateStore.ReadAsync(db);
        if (stage == InitialSetupStage.Completed) return;
        if (stage != InitialSetupStage.Staff) throw new InvalidOperationException("Guarde primero los datos del negocio.");
        var company = (await ReadAppSettingsAsync(db)).Company;
        _ = new InitialBusinessProfile(company.CommercialName, company.Phone, company.Email, company.Address).Validated();
        await SetValueAsync(db, InitialSetupStateStore.Key, nameof(InitialSetupStage.Completed));
        await SetValueAsync(db, "IsOnboardingCompleted", "true");
        CommercialLedger.Journal(db, actor, Guid.NewGuid().ToString("N"), "INITIAL_SETUP_COMPLETED", InitialSetupStateStore.Key, 0, "Configuración inicial finalizada");
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
