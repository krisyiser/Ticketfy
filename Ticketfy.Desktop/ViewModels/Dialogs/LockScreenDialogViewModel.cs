using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;

namespace Ticketfy.ViewModels.Dialogs;

public partial class LockScreenDialogViewModel : ObservableObject
{
    private readonly IUserRepository _userRepository;
    private readonly ISessionManager _sessionManager;
    private readonly Action _closeAction;

    [ObservableProperty] private string _enteredPin = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _cashierName = string.Empty;

    public LockScreenDialogViewModel(IUserRepository userRepository, ISessionManager sessionManager, Action closeAction)
    {
        _userRepository = userRepository;
        _sessionManager = sessionManager;
        _closeAction = closeAction;
        CashierName = _sessionManager.CurrentCashier?.FullName ?? "Terminal Bloqueada";
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
                ValidateAndUnlockAsync();
            }
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        EnteredPin = string.Empty;
        ErrorMessage = string.Empty;
    }

    private async void ValidateAndUnlockAsync()
    {
        bool isCurrentMatch = _sessionManager.CurrentCashier is { } currentUser
            && await _userRepository.ValidatePinAsync(currentUser.Username, EnteredPin) != null;
        bool isAdminMatch = await _userRepository.ValidateAdminPinAsync(EnteredPin);

        if (isCurrentMatch || isAdminMatch)
        {
            _sessionManager.UnlockTerminal();
            _closeAction?.Invoke();
        }
        else
        {
            ErrorMessage = "PIN Inválido — Acceso Denegado";
            EnteredPin = string.Empty;
        }
    }
}
