using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Base;

namespace Ticketfy.ViewModels;

public partial class SetupBusinessDataViewModel : ValidatableViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly Action _navigateToNextStep;
    private readonly Action? _navigateToPreviousStep;

    [ObservableProperty] private string _businessName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _address = string.Empty;

    [ObservableProperty] private string _errorMessage = string.Empty;

    public SetupBusinessDataViewModel(ISettingsService settingsService, Action navigateToNextStep, Action? navigateToPreviousStep = null)
    {
        _settingsService = settingsService;
        _navigateToNextStep = navigateToNextStep;
        _navigateToPreviousStep = navigateToPreviousStep;

        _ = LoadExistingSettingsAsync();
    }

    private async Task LoadExistingSettingsAsync()
    {
        try
        {
            var appSettings = await _settingsService.GetAppSettingsAsync();
            if (appSettings?.Company != null)
            {
                if (!string.IsNullOrEmpty(appSettings.Company.CommercialName))
                    BusinessName = appSettings.Company.CommercialName;
                if (!string.IsNullOrEmpty(appSettings.Company.Phone))
                    Phone = appSettings.Company.Phone;
                if (!string.IsNullOrEmpty(appSettings.Company.Email))
                    Email = appSettings.Company.Email;
                if (!string.IsNullOrEmpty(appSettings.Company.Address))
                    Address = appSettings.Company.Address;
            }

            if (string.IsNullOrEmpty(BusinessName))
                BusinessName = await _settingsService.GetAsync("BusinessName") ?? string.Empty;
            if (string.IsNullOrEmpty(Phone))
                Phone = await _settingsService.GetAsync("BusinessPhone") ?? string.Empty;
            if (string.IsNullOrEmpty(Email))
                Email = await _settingsService.GetAsync("BusinessEmail") ?? string.Empty;
            if (string.IsNullOrEmpty(Address))
                Address = await _settingsService.GetAsync("BusinessAddress") ?? string.Empty;

            // Sanitize legacy demo values so fields start completely empty with Watermarks
            if (BusinessName.Equals("TICKETFY! DEMO STORE", StringComparison.OrdinalIgnoreCase)) BusinessName = string.Empty;
            if (Phone.Equals("5512345678", StringComparison.OrdinalIgnoreCase)) Phone = string.Empty;
            if (Email.Equals("contacto@valcore.cloud", StringComparison.OrdinalIgnoreCase)) Email = string.Empty;
            if (Address.Equals("Av. Insurgentes Sur 1234, CDMX", StringComparison.OrdinalIgnoreCase)) Address = string.Empty;
        }
        catch { }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigateToPreviousStep?.Invoke();
    }

    [RelayCommand]
    private async Task SaveAndContinueAsync()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(BusinessName) || string.IsNullOrWhiteSpace(Phone))
        {
            ErrorMessage = "El Nombre del Negocio y el Teléfono son obligatorios.";
            return;
        }

        if (Phone.Trim().Length != 10)
        {
            ErrorMessage = "El Teléfono debe contener exactamente 10 dígitos.";
            return;
        }

        try
        {
            // Persist to both legacy keys and Empresa module keys for 100% full integration
            await _settingsService.SetAsync("BusinessName", BusinessName);
            await _settingsService.SetAsync("EmpresaNombreComercial", BusinessName);

            await _settingsService.SetAsync("BusinessEmail", Email);
            await _settingsService.SetAsync("EmpresaEmailContacto", Email);

            await _settingsService.SetAsync("BusinessPhone", Phone ?? string.Empty);
            await _settingsService.SetAsync("EmpresaTelefonoFijo", Phone ?? string.Empty);
            await _settingsService.SetAsync("EmpresaWhatsappContacto", Phone ?? string.Empty);

            await _settingsService.SetAsync("BusinessAddress", Address ?? string.Empty);
            await _settingsService.SetAsync("EmpresaCalleYNumero", Address ?? string.Empty);

            // Persistir objeto AppSettings global
            var appSettings = await _settingsService.GetAppSettingsAsync();
            appSettings.Company.CommercialName = BusinessName;
            appSettings.Company.Phone = Phone ?? string.Empty;
            appSettings.Company.Email = Email ?? string.Empty;
            appSettings.Company.Address = Address ?? string.Empty;
            await _settingsService.SaveAppSettingsAsync(appSettings);

            _navigateToNextStep();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al guardar configuración: {ex.Message}";
        }
    }
}
