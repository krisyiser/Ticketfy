using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public partial class SupervisorPinDialogViewModel : ObservableObject, Ticketfy.ViewModels.Navigation.IDismissibleDialog
{
    private readonly IUserRepository _userRepository;
    private readonly Action<bool, UserModel?> _callback;
    private bool _finished;
    private long _version;

    [ObservableProperty]
    private string _password = string.Empty;
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _actionTitle = "Autorización de Supervisor Requerida";

    public SupervisorPinDialogViewModel(IUserRepository userRepository, string actionTitle, Action<bool, UserModel?> callback)
    {
        _userRepository = userRepository;
        ActionTitle = actionTitle;
        _callback = callback;
    }

    [RelayCommand]
    private void ClearPin()
    {
        ++_version;
        Password = string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_finished) return;
        _finished = true; ++_version; Password = string.Empty;
        _callback?.Invoke(false, null);
    }
    public void OnDismissed() => Cancel();

    partial void OnUsernameChanged(string value) => ClearPin();

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (_finished || IsVerifying) return;
        long version = _version;
        if (string.IsNullOrWhiteSpace(Username)) { ErrorMessage = "Indique el usuario del supervisor."; Password = ""; return; }
        string secret = Password;
        Password = ""; ErrorMessage = ""; IsVerifying = true;
        try
        {
            var user = await _userRepository.VerifyPasswordAsync(Username, secret, supervisorOnly: true,
                isRequestCurrent: () => !_finished && version == _version);
            if (_finished || version != _version) return;
            if (user is not null && user.Role == SystemRole.ADMIN)
            {
                _finished = true;
                _callback(true, user);
            }
            else ErrorMessage = "Contraseña incorrecta, cuenta bloqueada o sin autorización de supervisor. Si requiere actualizar su clave, hágalo desde la pantalla de acceso. El PIN no autoriza esta acción.";
        }
        catch (Exception)
        {
            if (!_finished && version == _version) ErrorMessage = "No se pudo verificar la autorización.";
        }
        finally { IsVerifying = false; }
    }
}
