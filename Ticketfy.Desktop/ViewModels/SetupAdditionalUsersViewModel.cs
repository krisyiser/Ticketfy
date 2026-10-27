using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Entities;
using Ticketfy.ViewModels.Base;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class SetupAdditionalUsersViewModel : ValidatableViewModelBase
{
    private readonly IUserRepository _userRepository;
    private readonly Func<Task> _finishSetup;
    private readonly Action? _navigateToPreviousStep;
    private bool _departed;
    [ObservableProperty] private bool _isWorking;
    public bool CanEdit => !IsWorking && !_departed;
    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); CreateUserCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged(); GoBackCommand.NotifyCanExecuteChanged();
    }
    public void Deactivate()
    {
        _departed = true; FullName = Username = Password = PasswordHint = Pin1 = Pin2 = Pin3 = Pin4 = ErrorMessage = SuccessMessage = "";
        OnIsWorkingChanged(IsWorking);
    }

    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _passwordHint = string.Empty;

    [ObservableProperty] private string _pin1 = string.Empty;
    [ObservableProperty] private string _pin2 = string.Empty;
    [ObservableProperty] private string _pin3 = string.Empty;
    [ObservableProperty] private string _pin4 = string.Empty;

    public string Pin => $"{Pin1}{Pin2}{Pin3}{Pin4}";

    [ObservableProperty] private UserRole _selectedRole = UserRole.Cajero;
    [ObservableProperty] private ObservableCollection<UserRole> _availableRoles = new(new[] { UserRole.Cajero, UserRole.Gerente });

    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _successMessage = string.Empty;

    public SetupAdditionalUsersViewModel(IUserRepository userRepository, Func<Task> finishSetup, Action? navigateToPreviousStep = null)
    {
        _userRepository = userRepository;
        _finishSetup = finishSetup;
        _navigateToPreviousStep = navigateToPreviousStep;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void GoBack()
    {
        if (CanEdit) _navigateToPreviousStep?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task CreateUserAsync()
    {
        if (!CanEdit) return;
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(FullName) ||
            string.IsNullOrWhiteSpace(Username) ||
            string.IsNullOrWhiteSpace(Password) ||
            string.IsNullOrWhiteSpace(Pin))
        {
            ErrorMessage = "Todos los campos obligatorios deben ser llenados.";
            return;
        }

        if (Pin.Length != 4 || !Pin.All(char.IsAsciiDigit))
        {
            ErrorMessage = "El PIN debe ser de exactamente 4 dígitos numéricos.";
            return;
        }

        try
        {
            IsWorking = true;
            var user = new UserAccountDraft
            {
                Id = Guid.NewGuid(),
                FullName = FullName,
                Username = Username,
                Password = Password,
                Pin = Pin,
                Role = SelectedRole == UserRole.Admin ? "ADMINISTRADOR" : (SelectedRole == UserRole.Gerente ? "GERENTE" : "CAJERO"),
                IsActive = true
            };

            await _userRepository.CreateUserAsync(user);
            if (_departed) return;

            SuccessMessage = $"Usuario {Username} creado exitosamente.";

            // Clear fields for another user
            FullName = string.Empty;
            Username = string.Empty;
            Password = string.Empty;
            PasswordHint = string.Empty;
            Pin1 = string.Empty;
            Pin2 = string.Empty;
            Pin3 = string.Empty;
            Pin4 = string.Empty;
        }
        catch (Exception ex)
        {
            if (!_departed) ErrorMessage = $"Error al crear el usuario: {ex.Message}";
        }
        finally { IsWorking = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SkipAsync()
    {
        if (!CanEdit) return;
        IsWorking = true; ErrorMessage = "";
        try { await _finishSetup(); }
        catch (Exception ex) { if (!_departed) ErrorMessage = "No se confirmó la finalización. Puede reintentar. " + ex.Message; }
        finally { IsWorking = false; }
    }
}
