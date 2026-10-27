using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;

namespace Ticketfy.ViewModels.Dialogs;

public partial class LockScreenDialogViewModel : ObservableObject, Ticketfy.ViewModels.Navigation.IDismissibleDialog
{
    private readonly Ticketfy.ViewModels.Shell.LockScreenViewModel _unlock;
    private readonly ISessionManager _sessionManager;
    private readonly Action _closeAction;
    private bool _dismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedPin))]
    private string _enteredPin = string.Empty;
    public string MaskedPin => new('•', EnteredPin.Length);
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _cashierName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _usePassword = true;
    partial void OnUsePasswordChanged(bool value) { _unlock.UsePassword = value; ClearPin(); Password = ""; }

    public LockScreenDialogViewModel(IUserRepository userRepository, ISessionManager sessionManager, Action closeAction)
    {
        _unlock = new(userRepository, sessionManager);
        _sessionManager = sessionManager;
        _closeAction = closeAction;
        CashierName = _sessionManager.CurrentCashier?.FullName ?? "Terminal Bloqueada";
    }

    [RelayCommand]
    private async Task AppendPinDigitAsync(string digit)
    {
        if (!_dismissed && !UsePassword && digit.Length == 1 && digit[0] is >= '0' and <= '9' && EnteredPin.Length < 4)
        {
            EnteredPin += digit;
            ErrorMessage = string.Empty;
            if (EnteredPin.Length == 4)
            {
                await ValidateAndUnlockAsync();
            }
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        EnteredPin = string.Empty;
        ErrorMessage = string.Empty;
    }

    private async Task ValidateAndUnlockAsync()
    {
        if (_dismissed) return;
        _unlock.UsePassword = UsePassword;
        _unlock.UnlockPin = UsePassword ? Password : EnteredPin;
        EnteredPin = Password = string.Empty;
        await _unlock.UnlockTerminalCommand.ExecuteAsync(null);
        if (_dismissed) return;
        UsePassword = _unlock.UsePassword;
        ErrorMessage = _unlock.UnlockErrorMessage;
        if (_unlock.LastUnlockSucceeded)
        {
            OnDismissed();
            _closeAction?.Invoke();
        }
    }
    [RelayCommand]
    private Task VerifyPasswordAsync() => ValidateAndUnlockAsync();
    public void OnDismissed() { _dismissed = true; EnteredPin = Password = ""; _unlock.Dispose(); }
}
