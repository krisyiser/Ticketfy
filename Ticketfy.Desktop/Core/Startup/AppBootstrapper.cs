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
using Ticketfy.Services.Audit;

namespace Ticketfy.Core.Startup;

/// <summary>
/// Orchestrates the startup sequence: DB initialization, DI container setup,
/// and dependency-injection container setup.
/// </summary>
public static class AppBootstrapper
{
    public static async Task<IServiceProvider> BootstrapServicesAsync()
    {
        PasswordBlocklist.VerifyIntegrity();
        AppDataPaths.EnsureRequiredDirectories();
        var startupBackup = new BackupService();
        await startupBackup.ApplyPendingRestoreAsync();
        await DatabaseInitializer.InitializeAsync();

        string password = SecurityManager.GetMasterKey();
        var health = new MaintenanceHealth();
        var options = CreateCommercialOptions(
            CommercialDatabaseConnection.Create(AppDataPaths.DatabaseFile, password), health);
        var factory = new AppDbContextFactoryImpl<AppDbContext>(options);
        var auditFactory = new AppDbContextFactoryImpl<AuditDbContext>(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite($"Data Source={AppDataPaths.AuditDatabaseFile};").Options);
        Ticketfy.Core.Models.Settings.AppSettings appSettings;
        await using (var context = new AppDbContext(options))
        {
            appSettings = await new SettingsService(context).GetAppSettingsAsync();
        }

        string backupDirectory = ResolveBackupDirectory(appSettings.System.BackupPath);
        Ticketfy.Core.Helpers.BusinessTime.Configure(appSettings.Company.TimeZoneId);
        var backupService = new BackupService(backupDirectory: backupDirectory);
        var backupPolicy = new BackupPolicy(
            appSettings.System.AutomaticBackupsEnabled,
            TimeSpan.FromHours(Math.Clamp(appSettings.System.BackupIntervalHours, 1, 168)),
            Math.Clamp(appSettings.System.BackupRetentionCount, 1, 365),
            TimeSpan.FromDays(Math.Clamp(appSettings.System.BackupRetentionDays, 1, 3650)),
            appSettings.System.VerifyAutomaticBackups);
        health.ConfigureBackups(backupPolicy);
        health.Started(MaintenanceTask.Backups);
        BackupMaintenanceResult maintenance;
        try { maintenance = await backupService.RunMaintenanceAsync(backupPolicy); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Startup backup maintenance failed; periodic maintenance will retry");
            maintenance = new(true, false, "No se pudo completar el respaldo automático.");
        }
        health.BackupResult(backupPolicy, maintenance);
        if (maintenance.Executed && !maintenance.Succeeded)
            Serilog.Log.Warning("Startup backup maintenance did not complete: {Message}", maintenance.Message);

        var diagnostics = new DiagnosticExportService(backupDirectory: backupDirectory, maintenance: health.GetSnapshot);
        var telemetry = new LocalOperationalTelemetry(appSettings.System.TelemetryConsent);
        await telemetry.TrackAsync("application.started", succeeded: true);
        if (maintenance.Executed)
            await telemetry.TrackAsync("backup.maintenance", maintenance.Succeeded);
        return BuildServices(backupService, backupPolicy, diagnostics, telemetry,
            background: new(factory, auditFactory, new AuditService(auditFactory)), health: health);
    }

    internal static DbContextOptions<AppDbContext> CreateCommercialOptions(string connectionString, MaintenanceHealth health) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString)
            .AddInterceptors(new Ticketfy.Data.Interceptors.SlowQueryInterceptor())
            .AddInterceptors(Ticketfy.Data.Interceptors.MaintenanceWriteGuard.Create(health)).Options;

    internal static ServiceProvider BuildServices(
        IBackupService backupService,
        BackupPolicy backupPolicy,
        IDiagnosticExportService diagnostics,
        IOperationalTelemetry telemetry,
        TimeProvider? clock = null,
        BackgroundRuntimeDependencies? background = null,
        MaintenanceHealth? health = null)
    {
        health ??= new MaintenanceHealth(clock);
        health.ConfigureBackups(backupPolicy);
        var services = new ServiceCollection();
        services.AddSingleton(health);
        services.AddSingleton<IBackupService>(backupService);
        services.AddSingleton<IDiagnosticExportService>(diagnostics);
        services.AddSingleton<IOperationalTelemetry>(telemetry);
        // A factory-created singleton is owned and stopped by the container at exit.
        services.AddSingleton(_ => new BackupMaintenanceWorker(backupService, backupPolicy, telemetry, clock: clock, health: health));
        if (background is not null)
        {
            services.AddSingleton(background.Commercial);
            services.AddSingleton(background.Audit);
            services.AddSingleton(background.Integrity);
            services.AddSingleton<CoOccurrenceQueue>();
            services.AddSingleton(p => new CoOccurrenceWorker(p.GetRequiredService<CoOccurrenceQueue>(), background.Commercial, clock, health));
            services.AddSingleton(_ => new AlertBackgroundWorker(background.Commercial, clock: clock, health: health));
            services.AddSingleton(_ => new AuditVerificationCoordinator(background.Integrity, health));
            services.AddSingleton(p => new AuditArchivingWorker(p.GetRequiredService<AuditVerificationCoordinator>(), clock: clock));
        }
        services.AddSingleton<IFacturamaService>(_ =>
            LocalDevelopmentMode.FacturamaEnabled
                ? new FacturamaService(new System.Net.Http.HttpClient())
                : new UnavailableFacturamaService());

        var provider = services.BuildServiceProvider();
        try
        {
            provider.GetRequiredService<BackupMaintenanceWorker>().Start();
            if (background is not null)
            {
                provider.GetRequiredService<AuditArchivingWorker>().Start();
                provider.GetRequiredService<AlertBackgroundWorker>().Start();
                provider.GetRequiredService<CoOccurrenceWorker>().Start();
            }
            return provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    private static string ResolveBackupDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return AppDataPaths.BackupDirectory;
        string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : AppDataPaths.BackupDirectory;
    }
}

internal sealed record BackgroundRuntimeDependencies(IDbContextFactory<AppDbContext> Commercial,
    IDbContextFactory<AuditDbContext> Audit, IAuditService Integrity);
