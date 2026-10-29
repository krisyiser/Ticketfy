using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Entities;
using Ticketfy.ViewModels.Base;
using Ticketfy.Core.Services;
using Ticketfy.Core.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class FirstTimeSetupViewModel : ValidatableViewModelBase
{
    public string PasswordHelp => Ticketfy.Services.Security.AccountInputPolicy.PasswordHelp;
    private readonly IUserRepository _userRepository;
    private readonly IDialogService _dialogService;
    private readonly Action _navigateToNextStep;
    private readonly Action? _navigateToPreviousStep;
    private readonly Action<UserModel>? _onAdminCreated;
    private Guid _adminId = Guid.NewGuid();
    private bool _profileReady;
    private bool _departed;
    [ObservableProperty] private bool _isProfileLoading = true;
    [ObservableProperty] private bool _isSaving;
    public bool CanCreate => _profileReady && !IsProfileLoading && !IsSaving && !_departed;
    public bool CanGoBack => !IsProfileLoading && !IsSaving && !_departed;
    partial void OnIsProfileLoadingChanged(bool value) => NotifyState();
    partial void OnIsSavingChanged(bool value) => NotifyState();
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanCreate)); OnPropertyChanged(nameof(CanGoBack));
        CreateAdminAccountCommand.NotifyCanExecuteChanged(); GoBackCommand.NotifyCanExecuteChanged();
    }
    public void Deactivate()
    {
        _departed = true; AdminFullName = AdminUsername = AdminPassword = AdminPin1 = AdminPin2 = AdminPin3 = AdminPin4 = ErrorMessage = "";
        NotifyState();
    }
    public Task InitializationTask { get; }

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

    public FirstTimeSetupViewModel(IUserRepository userRepository, IDialogService dialogService, Action navigateToNextStep, Action? navigateToPreviousStep = null, Action<UserModel>? onAdminCreated = null)
    {
        _userRepository = userRepository;
        _dialogService = dialogService;
        _navigateToNextStep = navigateToNextStep;
        _navigateToPreviousStep = navigateToPreviousStep;
        _onAdminCreated = onAdminCreated;

        InitializationTask = LoadExistingAdminUserAsync();
    }

    private async Task LoadExistingAdminUserAsync()
    {
        try
        {
            var admin = await _userRepository.GetAdminProfileAsync();
            if (_departed) return;
            if (admin != null)
            {
                _adminId = Guid.Parse(admin.Id);
                AdminFullName = admin.FullName;
                AdminUsername = admin.Username;
                PasswordHint = string.Empty;
                // PINs are never read back into the UI. Entering a new PIN is
                // an explicit credential rotation operation.
            }
            _profileReady = true;
        }
        catch { if (!_departed) ErrorMessage = "No se pudo verificar la cuenta administradora existente. Vuelva a abrir la configuración con una sesión autorizada."; }
        finally { IsProfileLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CanGoBack) _navigateToPreviousStep?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAdminAccountAsync()
    {
        if (!CanCreate)
        {
            ErrorMessage = "Espere la verificación de la cuenta. Si falla, vuelva a abrir la configuración.";
            return;
        }
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(AdminFullName) ||
            string.IsNullOrWhiteSpace(AdminUsername) ||
            string.IsNullOrWhiteSpace(AdminPassword) ||
            string.IsNullOrWhiteSpace(AdminPin))
        {
            ErrorMessage = "Todos los campos son obligatorios.";
            return;
        }

        if (AdminPin.Length != 4 || !AdminPin.All(char.IsAsciiDigit))
        {
            ErrorMessage = "El PIN debe ser de exactamente 4 dígitos numéricos.";
            return;
        }

        try
        {
            IsSaving = true;
            var adminUser = new UserAccountDraft
            {
                Id = _adminId,
                FullName = AdminFullName,
                Username = AdminUsername,
                Password = AdminPassword,
                Pin = AdminPin,
                Role = "ADMINISTRADOR",
                IsActive = true
            };

            var saved = await _userRepository.SaveOrUpdateAdminUserAsync(adminUser);
            if (_departed) return;
            AdminPassword = AdminPin1 = AdminPin2 = AdminPin3 = AdminPin4 = "";
            _onAdminCreated?.Invoke(saved);

            // Advance to the next wizard step instead of logging in directly
            _navigateToNextStep();
        }
        catch (Exception ex)
        {
            if (!_departed) ErrorMessage = $"Error al crear la cuenta: {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}
