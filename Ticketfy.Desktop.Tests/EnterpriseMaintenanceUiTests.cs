using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ticketfy.Controls;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Shell;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseBackupWorkerTests
{
    [AvaloniaFact]
    public async Task MaintenanceUi_ShowsBlockedStateAndBoundReadOnlyRetryCannotHideFailedVerification()
    {
        int attempts = 0;
        var health = new MaintenanceHealth();
        await using var verification = new AuditVerificationCoordinator(new AuditWorkerStub(_ => Task.FromResult(++attempts > 1)), health);
        using var vm = new MaintenanceStatusViewModel(health, verification, new DiagnosticsStub());
        var control = new MaintenanceStatusControl { DataContext = vm };
        var window = new Window { Content = control, Width = 900, Height = 600 }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = control.FindControl<Button>("VerifyMaintenanceButton")!;
            Assert.Same(vm.VerifyCommand, button.Command);
            Assert.True(vm.WritesBlocked); Assert.True(vm.HasWarnings); Assert.True(button.IsEnabled);
            Assert.Equal(4, vm.Tasks.Count);
            Assert.Contains("no lo repita", vm.Guidance);
            await vm.VerifyCommand.ExecuteAsync(null);
            Assert.True(vm.WritesBlocked); Assert.Contains("bloqueo continúa", vm.Feedback);
            await vm.VerifyCommand.ExecuteAsync(null);
            Assert.False(vm.WritesBlocked);
            Assert.False(vm.VerifyCommand.CanExecute(null));
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEffectivelyEnabled);
            Assert.Contains("sin modificar", vm.Feedback);
            Assert.True(vm.HasWarnings); // A successful audit must not claim an unconfirmed backup exists.
            health.Succeeded(MaintenanceTask.Backups);
            health.Succeeded(MaintenanceTask.StockAlerts);
            health.Succeeded(MaintenanceTask.Recommendations);
            vm.Refresh();
            Assert.False(vm.HasWarnings);
            health.ConfigureBackups(BackupPolicy.Default with { Enabled = false, VerifyAfterCreation = false });
            vm.Refresh();
            Assert.False(vm.WritesBlocked); Assert.True(vm.HasWarnings);
            Assert.Contains(vm.Tasks, row => row.State.Contains("Desactivado") && row.Warning.Contains("desactivada"));
            await vm.ExportDiagnosticCommand.ExecuteAsync(null);
            Assert.Contains("No se pudo exportar", vm.Feedback);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MaintenanceUi_DisposalCancelsOnlyItsWaiterAndRejectsLateResults()
    {
        var entered = Signal(); var release = Signal(); int calls = 0;
        var health = new MaintenanceHealth();
        await using var verification = new AuditVerificationCoordinator(new AuditWorkerStub(async token =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await release.Task.WaitAsync(token); return true;
        }), health);
        var vm = new MaintenanceStatusViewModel(health, verification, new DiagnosticsStub());
        var pending = vm.VerifyCommand.ExecuteAsync(null);
        await Wait(entered.Task); vm.Refresh();
        Assert.False(vm.CanRetry);
        var independent = verification.VerifyAsync();
        vm.Dispose(); vm.Dispose();
        await pending;
        release.SetResult(); Assert.True(await independent);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.WritesBlocked); Assert.Empty(vm.Feedback); Assert.False(vm.CanRetry);
        Assert.Equal(1, calls); Assert.False(health.GetSnapshot().WritesBlocked);
    }

    [AvaloniaFact]
    public async Task MaintenanceUi_FreshnessRefreshAndDiagnosticExportRemainAvailableWithoutChangingSafety()
    {
        var clock = new HealthClock(); var health = new MaintenanceHealth(clock);
        health.Succeeded(MaintenanceTask.Audit);
        await using var verification = new AuditVerificationCoordinator(new AuditWorkerStub(_ => Task.FromResult(true)), health);
        var diagnostic = new HealthDiagnosticStub();
        using var vm = new MaintenanceStatusViewModel(health, verification, diagnostic);
        clock.Now += TimeSpan.FromHours(27); vm.Refresh();
        Assert.True(vm.WritesBlocked); Assert.True(vm.CanRetry);
        await vm.ExportDiagnosticCommand.ExecuteAsync(null);
        Assert.Equal(1, diagnostic.Calls); Assert.Contains("synthetic-diagnostic.zip", vm.Feedback);
        Assert.True(vm.WritesBlocked); // Export is not a waiver of the audit gate.
        await vm.VerifyCommand.ExecuteAsync(null);
        Assert.False(vm.WritesBlocked);
    }

    private sealed class HealthDiagnosticStub : IDiagnosticExportService
    {
        public int Calls { get; private set; }
        public Task<DiagnosticExportResult> ExportAsync(string? destinationDirectory = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(new DiagnosticExportResult("synthetic-diagnostic.zip", new string('A', 64)));
        }
    }
}
