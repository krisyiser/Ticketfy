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

public partial class SwitchUserPinDialogViewModel : ObservableObject, Ticketfy.ViewModels.Navigation.IDismissibleDialog
{
    private readonly IUserRepository? _userRepository;
    private readonly ISessionManager _sessionManager;
    private readonly Action<bool>? _resultCallback;
    private readonly long _sessionVersion;
    private long _requestVersion;
    private bool _finished;
    public Task InitializationTask { get; }
    private bool IsCurrent => !_finished && _sessionVersion == _sessionManager.SessionVersion && !_sessionManager.IsTerminalLocked;

    [ObservableProperty] private ObservableCollection<UserModel> _availableUsers = new();
    [ObservableProperty] private UserModel? _selectedUser;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedPin))]
    private string _enteredPin = string.Empty;
    public string MaskedPin => new('•', EnteredPin.Length);
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _usePassword = true;
    [ObservableProperty] private bool _isVerifying;
    partial void OnUsePasswordChanged(bool value) => ClearPin();

    public SwitchUserPinDialogViewModel(ISessionManager sessionManager, IUserRepository? userRepository = null, Action<bool>? resultCallback = null)
    {
        _sessionManager = sessionManager;
        _sessionVersion = sessionManager.SessionVersion;
        _userRepository = userRepository;
        _resultCallback = resultCallback;
        InitializationTask = LoadUsersAsync();
    }

    public SwitchUserPinDialogViewModel(IUserRepository userRepository, ISessionManager sessionManager, Action closeAction)
        : this(sessionManager, userRepository, _ => closeAction()) { }

    private async Task LoadUsersAsync()
    {
        try
        {
            if (_userRepository is null) throw new InvalidOperationException();
            var users = await _userRepository.GetActiveUsersAsync();
            if (!IsCurrent) return;
            AvailableUsers = new(users);
            SelectedUser = AvailableUsers.FirstOrDefault(u => u.Id == _sessionManager.CurrentCashier?.Id) ?? AvailableUsers.FirstOrDefault();
        }
        catch
        {
            if (IsCurrent) { AvailableUsers.Clear(); ErrorMessage = "No se pudo consultar el directorio de usuarios."; }
        }
    }
    partial void OnSelectedUserChanged(UserModel? value) => ClearPin();

    [RelayCommand]
    private async Task AppendPinDigitAsync(string digit)
    {
        if (IsCurrent && !UsePassword && !IsVerifying && EnteredPin.Length < 4 && digit.Length == 1 && char.IsAsciiDigit(digit[0]))
        {
            EnteredPin += digit;
            ErrorMessage = string.Empty;
            if (EnteredPin.Length == 4)
            {
                await ValidateAndSwitchAsync(false);
            }
        }
    }

    [RelayCommand]
    private void ClearPin()
    {
        ++_requestVersion;
        EnteredPin = string.Empty;
        Password = string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void CloseModal() => OnDismissed();
    public void OnDismissed()
    {
        if (_finished) return;
        _finished = true;
        ClearPin(); AvailableUsers.Clear(); SelectedUser = null;
        _resultCallback?.Invoke(false);
    }

    [RelayCommand]
    private Task VerifyPasswordAsync() => ValidateAndSwitchAsync(true);

    private async Task ValidateAndSwitchAsync(bool password)
    {
        if (!IsCurrent || IsVerifying) return;
        var selected = SelectedUser;
        long version = _requestVersion;
        if (selected is null || _userRepository is null) { EnteredPin = ""; ErrorMessage = "Seleccione un usuario."; return; }
        string secret = password ? Password : EnteredPin;
        Password = EnteredPin = ""; IsVerifying = true;
        try
        {
            var verified = password
                ? await _userRepository.VerifyPasswordAsync(selected.Username, secret,
                    isRequestCurrent: () => IsCurrent && version == _requestVersion && SelectedUser == selected)
                : await _userRepository.ValidatePinAsync(selected.Username, secret);
            if (!IsCurrent || version != _requestVersion || SelectedUser != selected) return;
            EnteredPin = "";
            if (verified is null || verified.Id != selected.Id || !verified.IsActive)
            {
                if (!password) UsePassword = true;
                ErrorMessage = "No se pudo verificar el acceso. Use su contraseña; el PIN requiere validarla en esta ejecución durante los últimos 15 minutos. Si necesita actualizarla, vuelva al inicio de sesión.";
                return;
            }
            _finished = true; AvailableUsers.Clear(); SelectedUser = null;
            _sessionManager.SwitchCashier(verified);
            _resultCallback?.Invoke(true);
        }
        catch
        {
            if (IsCurrent && version == _requestVersion) { EnteredPin = ""; ErrorMessage = "No se pudo verificar la identidad."; }
        }
        finally { IsVerifying = false; }
    }
}
