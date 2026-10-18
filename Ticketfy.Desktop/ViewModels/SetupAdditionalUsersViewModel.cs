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
    private readonly Action _finishSetup;
    private readonly Action? _navigateToPreviousStep;

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

    public SetupAdditionalUsersViewModel(IUserRepository userRepository, Action finishSetup, Action? navigateToPreviousStep = null)
    {
        _userRepository = userRepository;
        _finishSetup = finishSetup;
        _navigateToPreviousStep = navigateToPreviousStep;
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigateToPreviousStep?.Invoke();
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
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

        if (Pin.Length != 4 || !int.TryParse(Pin, out _))
        {
            ErrorMessage = "El PIN debe ser de exactamente 4 dígitos numéricos.";
            return;
        }

        try
        {
            string hash = CryptoHelper.HashPassword(Password);

            var user = new UserEntity
            {
                Id = Guid.NewGuid(),
                FullName = FullName,
                Username = Username,
                PasswordHash = hash,
                PasswordHint = string.Empty,
                PinCode = Pin,
                Role = SelectedRole,
                RoleString = SelectedRole == UserRole.Admin ? "ADMINISTRADOR" : (SelectedRole == UserRole.Gerente ? "GERENTE" : "CAJERO"),
                IsActive = true
            };

            await _userRepository.CreateUserAsync(user);

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
            ErrorMessage = $"Error al crear el usuario: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Skip()
    {
        _finishSetup();
    }
}
