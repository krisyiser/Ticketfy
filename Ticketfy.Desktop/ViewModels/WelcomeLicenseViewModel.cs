using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Threading.Tasks;
using Ticketfy.Services.Security;
using Ticketfy.ViewModels.Base;
using Ticketfy.Core.Configuration;

namespace Ticketfy.ViewModels;

/// <summary>
/// Friendly welcome & license activation ViewModel for OOBE onboarding.
/// Provides a welcoming introductory screen allowing license activation before first-time admin setup.
/// </summary>
public partial class WelcomeLicenseViewModel : ValidatableViewModelBase
{
    private readonly LicenseEnforcementService _licenseService;
    private readonly Action _navigateToNextStep;

    [ObservableProperty] private string _licenseTokenInput = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _successMessage = string.Empty;
    [ObservableProperty] private bool _isLicenseActive;
    [ObservableProperty] private string _licenseStatusText = "Modo Evaluación / Listo para Activar";

    public WelcomeLicenseViewModel(LicenseEnforcementService licenseService, Action navigateToNextStep)
    {
        _licenseService = licenseService;
        _navigateToNextStep = navigateToNextStep;

        CheckExistingLicense();
    }

    private void CheckExistingLicense()
    {
        try
        {
            string licensePath = AppDataPaths.LicenseFile;

            if (File.Exists(licensePath))
            {
                string token = File.ReadAllText(licensePath).Trim();
                if (!string.IsNullOrEmpty(token) && !_licenseService.IsSystemLocked())
                {
                    IsLicenseActive = true;
                    LicenseStatusText = "Licencia Activa y Verificada";
                    LicenseTokenInput = string.Empty;
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task ActivateLicenseAndContinueAsync()
    {
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;

        if (!string.IsNullOrWhiteSpace(LicenseTokenInput))
        {
            try
            {
                AppDataPaths.EnsureRequiredDirectories();
                _licenseService.InstallRenewal(LicenseTokenInput);

                if (_licenseService.IsSystemLocked())
                {
                    ErrorMessage = "La clave o token ingresado no es válido o ha caducado. Verifique su licencia.";
                    return;
                }
                else
                {
                    IsLicenseActive = true;
                    SuccessMessage = "¡Licencia activada con éxito!";
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error al guardar la licencia: {ex.Message}";
                return;
            }
        }

        _navigateToNextStep();
        await Task.CompletedTask;
    }
}
