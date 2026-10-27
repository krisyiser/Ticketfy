using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.ViewModels.Shell;

/// <summary>Read-only operational evidence. No repair, signature reset, credentials or database contents.</summary>
public sealed partial class MaintenanceStatusViewModel : ObservableObject, IDisposable
{
    private readonly MaintenanceHealth _health;
    private readonly AuditVerificationCoordinator _verification;
    private readonly IDiagnosticExportService _diagnostics;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    [ObservableProperty] private bool _writesBlocked;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _guidance = "";
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private IReadOnlyList<MaintenanceRow> _tasks = [];

    public MaintenanceStatusViewModel(MaintenanceHealth health, AuditVerificationCoordinator verification,
        IDiagnosticExportService diagnostics)
    {
        _health = health; _verification = verification; _diagnostics = diagnostics;
        _health.Changed += OnHealthChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Refresh());
        Refresh();
    }
    private void OnHealthChanged() => Dispatcher.UIThread.Post(Refresh);
    internal void Refresh()
    {
        if (_disposed) return;
        var snapshot = _health.GetSnapshot();
        var audit = snapshot.Tasks.Single(row => row.Task == MaintenanceTask.Audit);
        WritesBlocked = snapshot.WritesBlocked;
        CanRetry = WritesBlocked && audit.State != MaintenanceState.Running;
        HasWarnings = WritesBlocked || !snapshot.RestoreTestEnabled || snapshot.Tasks.Any(row =>
            row.Failure != MaintenanceFailure.None ||
            row.State is MaintenanceState.Failed or MaintenanceState.Disabled or MaintenanceState.Suspended or MaintenanceState.Stopped ||
            row.LastSuccessUtc is null);
        Summary = WritesBlocked ? "Operación protegida: escrituras bloqueadas" :
            HasWarnings ? "Mantenimiento: requiere atención" : "Mantenimiento operativo";
        Guidance = WritesBlocked ? MaintenanceHealth.WriteBlockedMessage :
            "Los estados corresponden a esta ejecución. Revise las advertencias antes de continuar la jornada; un respaldo fallido no bloquea las ventas.";
        Tasks = snapshot.Tasks.Select(row => new MaintenanceRow(
            row.Task switch
            {
                MaintenanceTask.Audit => "Integridad de auditoría",
                MaintenanceTask.Backups => "Respaldos automáticos",
                MaintenanceTask.StockAlerts => "Alertas de existencias",
                _ => "Recomendaciones"
            },
            Describe(row),
            row.LastSuccessUtc is { } success ? $"Último éxito en esta ejecución: {success:yyyy-MM-dd HH:mm:ss} UTC" : "Sin ejecución exitosa confirmada en esta sesión.",
            row.Task == MaintenanceTask.Backups && !snapshot.RestoreTestEnabled ? "Prueba automática de restauración desactivada." : ""
        )).ToArray();
        VerifyCommand.NotifyCanExecuteChanged();
    }
    private static string Describe(MaintenanceTaskStatus row) => row.State switch
    {
        MaintenanceState.Running when row.Failure != MaintenanceFailure.None => "Reintentando; la advertencia anterior sigue vigente.",
        MaintenanceState.Running => "En ejecución.",
        MaintenanceState.Healthy => "Última ejecución correcta.",
        MaintenanceState.Failed => "Falló. Conserve el diagnóstico y solicite soporte.",
        MaintenanceState.Suspended => "En espera de verificar la auditoría.",
        MaintenanceState.Disabled => "Desactivado; no hay protección automática.",
        MaintenanceState.Stopped => "Detenido.",
        _ => "Pendiente; todavía no hay resultado confirmado."
    };
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task VerifyAsync()
    {
        if (_disposed) return;
        Feedback = "";
        try
        {
            bool verified = await _verification.VerifyAsync(_lifetime.Token);
            if (!_disposed) Feedback = verified ? "Auditoría verificada sin modificar registros." : "No se pudo verificar la auditoría; el bloqueo continúa.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Manual read-only maintenance verification failed");
            if (!_disposed) Feedback = "Verificación no disponible. Conserve el diagnóstico y solicite soporte.";
        }
        finally { Refresh(); }
    }
    [RelayCommand]
    private async Task ExportDiagnosticAsync()
    {
        if (_disposed) return;
        Feedback = "";
        try
        {
            var result = await _diagnostics.ExportAsync(cancellationToken: _lifetime.Token);
            if (!_disposed) Feedback = $"Diagnóstico sin bases ni credenciales: {result.ArchivePath}";
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Maintenance diagnostic export failed");
            if (!_disposed) Feedback = "No se pudo exportar el diagnóstico. Revise espacio y permisos; solicite soporte.";
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _health.Changed -= OnHealthChanged; _timer.Stop();
        CanRetry = false; VerifyCommand.NotifyCanExecuteChanged();
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}

public sealed record MaintenanceRow(string Name, string State, string LastSuccess, string Warning);
