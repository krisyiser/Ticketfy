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
    private bool _sessionActive = true;
    private long _loadVersion;
    public bool HasLoadedData { get; private set; }
    private readonly IBackupService _backupService;
    private readonly IDiagnosticExportService _diagnosticService;
    private readonly IOperationalTelemetry _operationalTelemetry;
    private readonly Ticketfy.Services.Implementations.RecoveryBundleService? _recovery;

    [ObservableProperty] private AppSettings _state = new();
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isExecutingBackup = false;
    [ObservableProperty] private string _restoreBackupPath = string.Empty;
    [ObservableProperty] private bool _anonymousTelemetryEnabled;
    [ObservableProperty] private string _recoveryPath = string.Empty;
    [ObservableProperty] private string _recoveryPassphrase = string.Empty;
    [ObservableProperty] private string _recoveryPassphraseConfirmation = string.Empty;
    [ObservableProperty] private bool _recoveryCustodyConfirmed;
    public string[] BusinessTimeZones { get; } = ["America/Mexico_City", "America/Cancun", "America/Tijuana", "America/Hermosillo", "America/Mazatlan", "America/Chihuahua", "America/Ciudad_Juarez", "America/Matamoros", "America/Ojinaga"];

    public SeguridadSettingsViewModel(
        ISettingsService? settingsService = null,
        IBackupService? backupService = null,
        IDiagnosticExportService? diagnosticService = null,
        IOperationalTelemetry? operationalTelemetry = null,
        Ticketfy.Services.Implementations.RecoveryBundleService? recovery = null,
        bool loadOnCreate = true)
    {
        _settingsService = settingsService;
        _recovery = recovery;
        _backupService = backupService ?? new Ticketfy.Services.Implementations.BackupService();
        _diagnosticService = diagnosticService ?? new Ticketfy.Services.Implementations.DiagnosticExportService();
        _operationalTelemetry = operationalTelemetry ?? new Ticketfy.Services.Implementations.LocalOperationalTelemetry();
        if (loadOnCreate && _settingsService != null) _ = LoadAsync();
    }

    partial void OnRecoveryPassphraseChanged(string value) => RecoveryCustodyConfirmed = false;

    [RelayCommand]
    private async Task CreateRecoveryBundleAsync()
    {
        if (IsExecutingBackup) return;
        if (_recovery is null) { FeedbackMessage = "Recuperación integral no disponible en esta sesión."; return; }
        if (!RecoveryCustodyConfirmed || RecoveryPassphrase != RecoveryPassphraseConfirmation)
        { FeedbackMessage = "Confirme la frase y su custodia fuera de este equipo antes de exportar."; return; }
        if (!Path.IsPathFullyQualified(RecoveryPath)) { FeedbackMessage = "Indique una ruta absoluta para un archivo nuevo .tfyrecovery."; return; }
        IsExecutingBackup = true;
        FeedbackMessage = "Preparando paquete cifrado con base, auditoría y llaves...";
        try
        {
            string path = await _recovery.CreateAsync(RecoveryPath, RecoveryPassphrase);
            FeedbackMessage = $"Paquete integral creado: {path}\nGuárdelo fuera de este equipo y verifíquelo periódicamente. La frase no se conserva en Ticketfy.";
        }
        catch (Exception ex) { FeedbackMessage = "No se creó el paquete: " + ex.Message; }
        finally { ClearRecoverySecret(); IsExecutingBackup = false; }
    }

    [RelayCommand]
    private async Task VerifyRecoveryBundleAsync()
    {
        if (IsExecutingBackup) return;
        IsExecutingBackup = true;
        FeedbackMessage = "Verificando paquete en una copia temporal aislada...";
        try
        {
            var result = await Task.Run(() => Ticketfy.Services.Implementations.RecoveryBundleService.VerifyAsync(
                RecoveryPath, RecoveryPassphrase, Path.GetTempPath()));
            FeedbackMessage = result.Message;
        }
        catch (Exception ex) { FeedbackMessage = "No se verificó el paquete: " + ex.Message; }
        finally { ClearRecoverySecret(); IsExecutingBackup = false; }
    }

    private void ClearRecoverySecret()
    {
        RecoveryPassphrase = string.Empty;
        RecoveryPassphraseConfirmation = string.Empty;
        RecoveryCustodyConfirmed = false;
    }
    public void ClearSessionSecrets()
    {
        _sessionActive = false; HasLoadedData = false; State = new();
        ClearRecoverySecret();
        RecoveryPath = RestoreBackupPath = FeedbackMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SelectRecoveryBundleAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;
        var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar paquete integral para verificar",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Recuperación Ticketfy") { Patterns = ["*.tfyrecovery"] }]
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is string path) RecoveryPath = path;
    }

    [RelayCommand]
    private async Task SelectRecoveryDestinationAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null) return;
        var file = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Destino de un paquete integral NUEVO",
            SuggestedFileName = $"ticketfy-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tfyrecovery",
            DefaultExtension = "tfyrecovery",
            FileTypeChoices = [new FilePickerFileType("Recuperación Ticketfy") { Patterns = ["*.tfyrecovery"] }]
        });
        if (file?.TryGetLocalPath() is string path) RecoveryPath = path;
    }

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        HasLoadedData = false;
        if (_settingsService == null) return;
        try
        {
            var loaded = await _settingsService.GetAppSettingsAsync();
            if (!_sessionActive || version != _loadVersion) return;
            State = loaded;
            AnonymousTelemetryEnabled = State.System.TelemetryConsent == TelemetryConsent.AnonymousOperational;
            HasLoadedData = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading AppSettings in SeguridadSettingsViewModel");
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (_settingsService == null || !_sessionActive) return false;
        try
        {
            State.System.TelemetryConsent = AnonymousTelemetryEnabled
                ? TelemetryConsent.AnonymousOperational
                : TelemetryConsent.Disabled;
            await _settingsService.SaveSectionAsync(State, AppSettingsSection.Security);
            _operationalTelemetry.SetConsent(State.System.TelemetryConsent);
            FeedbackMessage = "Configuración guardada. Los cambios de ruta y programación de respaldos se aplican al reiniciar.";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed saving AppSettings in SeguridadSettingsViewModel");
            FeedbackMessage = ex is InvalidOperationException or TimeZoneNotFoundException ? ex.Message : "Error al guardar configuración de seguridad.";
            return false;
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
