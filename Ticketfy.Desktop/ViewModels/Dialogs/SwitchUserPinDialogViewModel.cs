using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;

namespace Ticketfy.ViewModels.Dialogs;

public partial class SwitchUserPinDialogViewModel : ObservableObject
{
    private readonly IUserRepository? _userRepository;
    private readonly ISessionManager _sessionManager;
    private readonly Action? _closeAction;
    private readonly Action<bool>? _resultCallback;

    [ObservableProperty] private ObservableCollection<UserModel> _availableUsers = new();
    [ObservableProperty] private UserModel? _selectedUser;
    [ObservableProperty] private string _enteredPin = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public SwitchUserPinDialogViewModel(ISessionManager sessionManager, IUserRepository? userRepository = null, Action<bool>? resultCallback = null)
    {
        _sessionManager = sessionManager;
        _userRepository = userRepository;
        _resultCallback = resultCallback;
        _ = LoadUsersAsync();
    }

    public SwitchUserPinDialogViewModel(IUserRepository userRepository, ISessionManager sessionManager, Action closeAction)
        : this(sessionManager, userRepository, (success) => { if (success) closeAction?.Invoke(); else closeAction?.Invoke(); })
    {
        _closeAction = closeAction;
    }

    private async Task LoadUsersAsync()
    {
        if (_userRepository != null)
        {
            var users = await _userRepository.GetActiveUsersAsync();
            AvailableUsers = new ObservableCollection<UserModel>(users);
        }
        else
        {
            ErrorMessage = "No se pudo acceder al directorio de usuarios.";
            AvailableUsers = new ObservableCollection<UserModel>();
        }

        // Pre-select the currently active cashier session user
        if (_sessionManager?.CurrentCashier != null)
        {
            var active = _sessionManager.CurrentCashier;
            SelectedUser = AvailableUsers.FirstOrDefault(u =>
                (u.Id != Guid.Empty && u.Id == active.Id)
                || (!string.IsNullOrWhiteSpace(u.Username) && u.Username.Equals(active.Username, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(u.FullName) && u.FullName.Equals(active.FullName, StringComparison.OrdinalIgnoreCase))
            ) ?? AvailableUsers.FirstOrDefault();
        }
        else
        {
            SelectedUser = AvailableUsers.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void AppendPinDigit(string digit)
    {
        if (EnteredPin.Length < 4)
        {
            EnteredPin += digit;
            ErrorMessage = string.Empty;
            if (EnteredPin.Length == 4)
            {
                ValidateAndSwitchAsync();
            }
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        EnteredPin = string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void CloseModal()
    {
        _resultCallback?.Invoke(false);
        _closeAction?.Invoke();
    }

    private async void ValidateAndSwitchAsync()
    {
        if (SelectedUser == null) return;

        bool isValid = false;
        UserModel? validatedUser = null;

        if (_userRepository != null)
        {
            string lookupKey = !string.IsNullOrWhiteSpace(SelectedUser.Username)
                ? SelectedUser.Username
                : SelectedUser.FullName;

            validatedUser = await _userRepository.ValidatePinAsync(lookupKey, EnteredPin);
            isValid = validatedUser != null;
        }
        else
        {
            ErrorMessage = "No se pudo validar el PIN.";
        }

        if (isValid && validatedUser != null)
        {
            _sessionManager.SwitchCashier(validatedUser);
            _resultCallback?.Invoke(true);
            _closeAction?.Invoke();
        }
        else
        {
            ErrorMessage = "PIN Incorrecto";
            EnteredPin = string.Empty;
        }
    }
}
