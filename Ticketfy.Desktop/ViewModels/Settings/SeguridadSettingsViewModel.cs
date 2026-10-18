using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Ticketfy.Core.Operations;

namespace Ticketfy.ViewModels.Settings;

/// <summary>
/// Strongly-typed ViewModel for Section 4: Sistema & Seguridad.
/// Configures SQLite backup paths, automatic closing backups, auto-lock timeouts and security PIN enforcement.
/// Provides immediate database backup execution and feedback.
/// </summary>
public partial class SeguridadSettingsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;
    private readonly IBackupService _backupService;
    private readonly IDiagnosticExportService _diagnosticService;
    private readonly IOperationalTelemetry _operationalTelemetry;

    [ObservableProperty] private AppSettings _state = new();
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isExecutingBackup = false;
    [ObservableProperty] private string _restoreBackupPath = string.Empty;
    [ObservableProperty] private bool _anonymousTelemetryEnabled;

    public SeguridadSettingsViewModel(
        ISettingsService? settingsService = null,
        IBackupService? backupService = null,
        IDiagnosticExportService? diagnosticService = null,
        IOperationalTelemetry? operationalTelemetry = null)
    {
        _settingsService = settingsService;
        _backupService = backupService ?? new Ticketfy.Services.Implementations.BackupService();
        _diagnosticService = diagnosticService ?? new Ticketfy.Services.Implementations.DiagnosticExportService();
        _operationalTelemetry = operationalTelemetry ?? new Ticketfy.Services.Implementations.LocalOperationalTelemetry();
        if (_settingsService != null) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_settingsService == null) return;
        try
        {
            State = await _settingsService.GetAppSettingsAsync();
            AnonymousTelemetryEnabled = State.System.TelemetryConsent == TelemetryConsent.AnonymousOperational;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading AppSettings in SeguridadSettingsViewModel");
        }
    }

    public async Task SaveAsync()
    {
        if (_settingsService == null) return;
        try
        {
            State.System.TelemetryConsent = AnonymousTelemetryEnabled
                ? TelemetryConsent.AnonymousOperational
                : TelemetryConsent.Disabled;
            await _settingsService.SaveAppSettingsAsync(State);
            _operationalTelemetry.SetConsent(State.System.TelemetryConsent);
            FeedbackMessage = "Configuración guardada. Los cambios de ruta y programación de respaldos se aplican al reiniciar.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving AppSettings in SeguridadSettingsViewModel");
            FeedbackMessage = "Error al guardar configuración de seguridad.";
        }
    }

    [RelayCommand]
    private async Task CreateBackupNowAsync()
    {
        IsExecutingBackup = true;
        FeedbackMessage = "Generando copia de seguridad de la base de datos...";
        try
        {
            string path = await _backupService.CreateBackupAsync();
            if (!string.IsNullOrEmpty(path))
            {
                FeedbackMessage = $"¡Copia de seguridad respaldada con éxito en:\n{path}";
            }
            else
            {
                FeedbackMessage = "Error al generar la copia de seguridad.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing immediate database backup");
            FeedbackMessage = "Error interno durante la generación de la copia de seguridad.";
        }
        finally
        {
            IsExecutingBackup = false;
        }
    }

    [RelayCommand]
    private async Task ValidateAndPrepareRestoreAsync()
    {
        if (string.IsNullOrWhiteSpace(RestoreBackupPath))
        {
            FeedbackMessage = "Indique la ruta completa del respaldo .db que desea restaurar.";
            return;
        }

        IsExecutingBackup = true;
        FeedbackMessage = "Validando integridad, esquema y recuperación temporal...";
        try
        {
            BackupValidationResult test = await _backupService.TestRestoreAsync(RestoreBackupPath);
            if (!test.IsValid)
            {
                FeedbackMessage = test.Message;
                return;
            }

            RestorePreparationResult prepared = await _backupService.PrepareRestoreAsync(RestoreBackupPath);
            FeedbackMessage = prepared.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed preparing guided database restore");
            FeedbackMessage = "No se pudo preparar la restauración: " + ex.Message;
        }
        finally
        {
            IsExecutingBackup = false;
        }
    }

    [RelayCommand]
    private async Task SelectRestoreBackupAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;

        var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar respaldo verificado de Ticketfy",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Respaldo SQLite de Ticketfy (*.db)") { Patterns = ["*.db"] }
            ]
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is string localPath)
            RestoreBackupPath = localPath;
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        IsExecutingBackup = true;
        try
        {
            DiagnosticExportResult result = await _diagnosticService.ExportAsync();
            FeedbackMessage = $"Diagnóstico sanitizado creado en:\n{result.ArchivePath}\nSHA-256: {result.Sha256}";
            await _operationalTelemetry.TrackAsync("diagnostics.exported", succeeded: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed exporting sanitized diagnostics");
            FeedbackMessage = "No se pudo exportar el diagnóstico.";
            await _operationalTelemetry.TrackAsync("diagnostics.exported", succeeded: false);
        }
        finally
        {
            IsExecutingBackup = false;
        }
    }
}
