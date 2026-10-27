using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

internal static class InitialSetupStateStore
{
    internal const string Key = "InitialSetup.Stage";

    // Called inside the same transaction as the first protected administrator.
    internal static async Task BeginIfEmptyAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync()) return;
        await SetAsync(db, Key, nameof(InitialSetupStage.Business));
        await SetAsync(db, "IsOnboardingCompleted", "false");
    }

    internal static async Task<InitialSetupStage> ReadAsync(AppDbContext db)
    {
        if (!await db.Users.AnyAsync()) return InitialSetupStage.NotStarted;
        string? value = await db.Settings.Where(s => s.Key == Key).Select(s => s.Value).SingleOrDefaultAsync();
        // Existing installations have no durable stage. Never reopen account creation for them.
        if (value is null) return InitialSetupStage.ExistingInstallation;
        return value switch
        {
            "Business" => InitialSetupStage.Business,
            "Staff" => InitialSetupStage.Staff,
            "Completed" => InitialSetupStage.Completed,
            _ => throw new InvalidDataException("El estado de configuración inicial no es válido. Conserve los datos y solicite soporte.")
        };
    }

    internal static async Task SetAsync(AppDbContext db, string key, string value)
    {
        var entry = await db.Settings.FindAsync(key);
        if (entry is null) db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else entry.Value = value;
    }
}
