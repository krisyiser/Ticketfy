using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Entities;
using Ticketfy.ViewModels.Base;
using Ticketfy.Core.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class FirstTimeSetupViewModel : ValidatableViewModelBase
{
    private readonly IUserRepository _userRepository;
    private readonly IDialogService _dialogService;
    private readonly Action _navigateToNextStep;
    private readonly Action? _navigateToPreviousStep;

    [ObservableProperty] private string _adminFullName = string.Empty;
    [ObservableProperty] private string _adminUsername = string.Empty;
    [ObservableProperty] private string _adminPassword = string.Empty;
    [ObservableProperty] private string _passwordHint = string.Empty;

    [ObservableProperty] private string _adminPin1 = string.Empty;
    [ObservableProperty] private string _adminPin2 = string.Empty;
    [ObservableProperty] private string _adminPin3 = string.Empty;
    [ObservableProperty] private string _adminPin4 = string.Empty;

    public string AdminPin => $"{AdminPin1}{AdminPin2}{AdminPin3}{AdminPin4}";

    [ObservableProperty] private string _errorMessage = string.Empty;

    public FirstTimeSetupViewModel(IUserRepository userRepository, IDialogService dialogService, Action navigateToNextStep, Action? navigateToPreviousStep = null)
    {
        _userRepository = userRepository;
        _dialogService = dialogService;
        _navigateToNextStep = navigateToNextStep;
        _navigateToPreviousStep = navigateToPreviousStep;

        _ = LoadExistingAdminUserAsync();
    }

    private async Task LoadExistingAdminUserAsync()
    {
        try
        {
            var admin = await _userRepository.GetAdminUserAsync();
            if (admin != null)
            {
                AdminFullName = admin.FullName;
                AdminUsername = admin.Username;
                PasswordHint = string.Empty;
                // PINs are never read back into the UI. Entering a new PIN is
                // an explicit credential rotation operation.
            }
        }
        catch { }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigateToPreviousStep?.Invoke();
    }

    [RelayCommand]
    private async Task CreateAdminAccountAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(AdminFullName) ||
            string.IsNullOrWhiteSpace(AdminUsername) ||
            string.IsNullOrWhiteSpace(AdminPassword) ||
            string.IsNullOrWhiteSpace(AdminPin))
        {
            ErrorMessage = "Todos los campos son obligatorios.";
            return;
        }

        if (AdminPin.Length != 4 || !int.TryParse(AdminPin, out _))
        {
            ErrorMessage = "El PIN debe ser de exactamente 4 dígitos numéricos.";
            return;
        }

        try
        {
            string hash = CryptoHelper.HashPassword(AdminPassword);

            var adminUser = new UserEntity
            {
                Id = Guid.NewGuid(),
                FullName = AdminFullName,
                Username = AdminUsername,
                PasswordHash = hash,
                PasswordHint = string.Empty,
                PinCode = AdminPin,
                Role = UserRole.Admin,
                RoleString = "ADMINISTRADOR",
                IsActive = true
            };

            await _userRepository.SaveOrUpdateAdminUserAsync(adminUser);

            // Advance to the next wizard step instead of logging in directly
            _navigateToNextStep();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al crear la cuenta: {ex.Message}";
        }
    }
}
