using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

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

    [ObservableProperty] private AppSettings _state = new();
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isExecutingBackup = false;

    public SeguridadSettingsViewModel(ISettingsService? settingsService = null, IBackupService? backupService = null)
    {
        _settingsService = settingsService;
        _backupService = backupService ?? new Ticketfy.Services.Implementations.BackupService();
        if (_settingsService != null) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_settingsService == null) return;
        try
        {
            State = await _settingsService.GetAppSettingsAsync();
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
            await _settingsService.SaveAppSettingsAsync(State);
            FeedbackMessage = "¡Configuración de seguridad guardada correctamente!";
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
}
