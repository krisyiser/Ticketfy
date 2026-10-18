using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using System;
using System.IO;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Operations;
using Ticketfy.Data;
using Ticketfy.Services.Security;

namespace Ticketfy.Core.Startup;

/// <summary>
/// Orchestrates the startup sequence: DB initialization, DI container setup,
/// and dependency-injection container setup.
/// </summary>
public static class AppBootstrapper
{
    public static async Task<IServiceProvider> BootstrapServicesAsync()
    {
        AppDataPaths.EnsureRequiredDirectories();
        var startupBackup = new BackupService();
        await startupBackup.ApplyPendingRestoreAsync();
        await DatabaseInitializer.InitializeAsync();

        string password = SecurityManager.GetMasterKey();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.DatabaseFile};Password={password};Cache=Shared;Mode=ReadWriteCreate;")
            .Options;
        Ticketfy.Core.Models.Settings.AppSettings appSettings;
        await using (var context = new AppDbContext(options))
        {
            appSettings = await new SettingsService(context).GetAppSettingsAsync();
        }

        string backupDirectory = ResolveBackupDirectory(appSettings.System.BackupPath);
        var backupService = new BackupService(backupDirectory: backupDirectory);
        var backupPolicy = new BackupPolicy(
            appSettings.System.AutomaticBackupsEnabled,
            TimeSpan.FromHours(Math.Clamp(appSettings.System.BackupIntervalHours, 1, 168)),
            Math.Clamp(appSettings.System.BackupRetentionCount, 1, 365),
            TimeSpan.FromDays(Math.Clamp(appSettings.System.BackupRetentionDays, 1, 3650)),
            appSettings.System.VerifyAutomaticBackups);
        BackupMaintenanceResult maintenance = await backupService.RunMaintenanceAsync(backupPolicy);
        if (maintenance.Executed && !maintenance.Succeeded)
            Serilog.Log.Warning("Startup backup maintenance did not complete: {Message}", maintenance.Message);

        var diagnostics = new DiagnosticExportService(backupDirectory: backupDirectory);
        var telemetry = new LocalOperationalTelemetry(appSettings.System.TelemetryConsent);
        await telemetry.TrackAsync("application.started", succeeded: true);
        if (maintenance.Executed)
            await telemetry.TrackAsync("backup.maintenance", maintenance.Succeeded);
        var maintenanceWorker = new BackupMaintenanceWorker(backupService, backupPolicy, telemetry);
        maintenanceWorker.Start();

        var services = new ServiceCollection();
        services.AddSingleton<IBackupService>(backupService);
        services.AddSingleton<IDiagnosticExportService>(diagnostics);
        services.AddSingleton<IOperationalTelemetry>(telemetry);
        services.AddSingleton(maintenanceWorker);
        services.AddSingleton<IFacturamaService>(_ =>
            LocalDevelopmentMode.FacturamaEnabled
                ? new FacturamaService(new System.Net.Http.HttpClient())
                : new UnavailableFacturamaService());

        return services.BuildServiceProvider();
    }

    private static string ResolveBackupDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return AppDataPaths.BackupDirectory;
        string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : AppDataPaths.BackupDirectory;
    }
}
