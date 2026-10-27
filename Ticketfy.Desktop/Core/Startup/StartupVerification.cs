using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Ticketfy.Core.Configuration;
using Ticketfy.Data;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Core.Startup;

/// <summary>Process-level release probe. Only accepts a new directory; never opens an existing installation.</summary>
internal static class StartupVerification
{
    internal static void Run(string[] args)
    {
        if (args.Length != 3 || args[0] != "--verify-startup" || args[1] != "--data-directory" ||
            !Path.IsPathFullyQualified(args[2]) || Directory.Exists(args[2]) || File.Exists(args[2]))
            throw new ArgumentException("La verificación requiere una carpeta absoluta nueva, que todavía no exista.");
        if (!LocalDevelopmentMode.IsEnabled || LocalDevelopmentMode.FacturamaEnabled || LocalDevelopmentMode.RemoteInfrastructureEnabled)
            throw new InvalidOperationException("La verificación sólo admite la edición local con integraciones aplazadas.");
        AppDataPaths.UseDataDirectory(args[2]);
        using var instance = new ApplicationInstanceLock(AppDataPaths.RootDirectory);
        if (!instance.IsAcquired) throw new InvalidOperationException("Laboratorio ocupado.");
        VerifyAsync().GetAwaiter().GetResult();
    }
    private static async Task VerifyAsync()
    {
        string value = Guid.NewGuid().ToString("N");
        int migrations = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            await using var services = (ServiceProvider)await AppBootstrapper.BootstrapServicesAsync();
            var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            if (await db.Sales.AnyAsync() || await db.Purchases.AnyAsync() || await db.Users.AnyAsync())
                throw new InvalidOperationException("El laboratorio no está vacío.");
            if ((await db.Database.GetPendingMigrationsAsync()).Any() || db.Database.HasPendingModelChanges())
                throw new InvalidOperationException("El esquema no coincide con el candidato.");
            migrations = (await db.Database.GetAppliedMigrationsAsync()).Count();
            if (pass == 0)
            {
                db.Settings.Add(new() { Key = "startup-verification", Value = value });
                await db.SaveChangesAsync();
                var backups = services.GetRequiredService<IBackupService>();
                var path = await backups.CreateBackupAsync("StartupVerification");
                if (!(await backups.TestRestoreAsync(path)).IsValid)
                    throw new InvalidOperationException("Falló la prueba de restauración del laboratorio.");
            }
            else if ((await db.Settings.SingleAsync(s => s.Key == "startup-verification")).Value != value)
                throw new InvalidOperationException("El dato sintético no persistió al reabrir.");
        }
        await File.WriteAllTextAsync(Path.Combine(AppDataPaths.RootDirectory, "startup-verification.json"),
            JsonSerializer.Serialize(new
            {
                Succeeded = true,
                Migrations = migrations,
                BootstrapPasses = 2,
                BackupRestorationVerified = true,
                FacturamaEnabled = false,
                PasswordPolicyVersion = Ticketfy.Services.Security.PasswordPolicy.CurrentVersion,
                PasswordBlocklistEntries = Ticketfy.Services.Security.PasswordBlocklist.Current.Count,
                PasswordBlocklistSha256 = Ticketfy.Services.Security.PasswordBlocklist.PackagedSha256,
                Version = Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion
            }));
    }
}
