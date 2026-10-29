using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Auth;
using Ticketfy.Core.Services;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    public string PasswordHelp => Ticketfy.Services.Security.AccountInputPolicy.PasswordHelp;
    private readonly AuthService _authService;
    private readonly ISessionManager _sessionManager;
    private readonly IDialogService _dialogService;
    private long _authenticationVersion;
    private bool _resettingFields;
    private bool _completed;

    [ObservableProperty] private bool _passwordChangeRequired;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmNewPassword = string.Empty;
    [ObservableProperty] private string _informationMessage = string.Empty;
    public bool CanLogin => !IsWorking && !PasswordChangeRequired && !_completed;
    public bool CanUpgrade => !IsWorking && PasswordChangeRequired && !_completed;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _hintMessage = string.Empty;

    [ObservableProperty]
    private bool _isHintVisible = false;

    public event Action? LoginSuccessful;

    public LoginViewModel(AuthService authService, ISessionManager sessionManager, IDialogService dialogService)
    {
        _authService = authService;
        _sessionManager = sessionManager;
        _dialogService = dialogService;
    }

    public void EndSession()
    {
        ++_authenticationVersion;
        _authService.Logout();
        _sessionManager.ClearSession();
        _completed = false;
        IsWorking = false;
        ResetFields(clearUsername: true);
        ErrorMessage = HintMessage = InformationMessage = string.Empty;
        IsHintVisible = false;
        NotifyCommands();
    }

    partial void OnUsernameChanged(string value)
    {
        if (_resettingFields) return;
        ++_authenticationVersion;
        _authService.Logout();
        _completed = false;
        IsWorking = false;
        ResetFields();
        ErrorMessage = InformationMessage = string.Empty;
        NotifyCommands();
    }

    private void ResetFields(bool clearUsername = false)
    {
        _resettingFields = true;
        try
        {
            if (clearUsername) Username = string.Empty;
            Password = NewPassword = ConfirmNewPassword = string.Empty;
            PasswordChangeRequired = false;
        }
        finally { _resettingFields = false; }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanUpgrade));
        LoginCommand.NotifyCanExecuteChanged(); UpgradePasswordCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (!CanLogin) return;
        var version = ++_authenticationVersion;
        _sessionManager.ClearSession();
        var sessionVersion = _sessionManager.SessionVersion;
        ErrorMessage = string.Empty;
        HintMessage = string.Empty;
        InformationMessage = string.Empty;
        IsHintVisible = false;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Usuario y contraseña requeridos.";
            return;
        }

        try
        {
            IsWorking = true; NotifyCommands();
            var authResult = await _authService.AuthenticateAsync(Username, Password,
                () => version == _authenticationVersion && sessionVersion == _sessionManager.SessionVersion);
            if (version != _authenticationVersion || sessionVersion != _sessionManager.SessionVersion) return;
            if (authResult.PasswordChangeRequired)
            {
                ResetFields();
                PasswordChangeRequired = true;
                InformationMessage = $"Actualice su contraseña antes de iniciar sesión. Vuelva a indicar la anterior. {PasswordHelp} El PIN anterior se deshabilitará.";
            }
            else if (authResult.IsSuccess && authResult.User != null)
            {
                _completed = true;
                ResetFields(clearUsername: true);
                _sessionManager.StartSession(authResult.User);
                LoginSuccessful?.Invoke();
            }
            else
            {
                ErrorMessage = "Credenciales incorrectas.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error unhandled during login attempt for user {UserReference}", Ticketfy.Logging.LogSanitizer.Pseudonymize(Username));
            if (version == _authenticationVersion && sessionVersion == _sessionManager.SessionVersion)
                ErrorMessage = "Error de conexión o autenticación en la base de datos.";
        }
        finally
        {
            if (version == _authenticationVersion) { Password = string.Empty; IsWorking = false; NotifyCommands(); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradePasswordAsync()
    {
        if (!CanUpgrade) return;
        ErrorMessage = string.Empty;
        if (string.IsNullOrEmpty(Password) || string.IsNullOrEmpty(NewPassword) || NewPassword != ConfirmNewPassword)
        { ErrorMessage = "Indique la contraseña anterior y confirme la misma contraseña nueva."; return; }
        var version = ++_authenticationVersion;
        IsWorking = true; NotifyCommands();
        _sessionManager.ClearSession();
        var sessionVersion = _sessionManager.SessionVersion;
        try
        {
            bool changed = await _authService.UpgradeOwnPasswordAsync(Username, Password, NewPassword);
            if (version != _authenticationVersion || sessionVersion != _sessionManager.SessionVersion) return;
            if (!changed) { ErrorMessage = "Credencial anterior incorrecta, cuenta inactiva o temporalmente bloqueada."; return; }
            ResetFields();
            InformationMessage = "Contraseña actualizada. Inicie sesión con la nueva. El PIN anterior quedó deshabilitado; un administrador puede registrar uno nuevo.";
        }
        catch (InvalidOperationException ex)
        {
            if (version == _authenticationVersion && sessionVersion == _sessionManager.SessionVersion) ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Password transition could not be confirmed");
            if (version == _authenticationVersion && sessionVersion == _sessionManager.SessionVersion)
                ErrorMessage = "No se pudo confirmar el cambio. Vuelva al acceso e intente iniciar sesión con la nueva contraseña antes de repetirlo.";
        }
        finally
        {
            if (version == _authenticationVersion)
            {
                Password = NewPassword = ConfirmNewPassword = string.Empty;
                IsWorking = false; NotifyCommands();
            }
        }
    }

    [RelayCommand]
    private void CancelPasswordChange()
    {
        ++_authenticationVersion;
        _authService.Logout();
        ResetFields();
        IsWorking = false;
        ErrorMessage = InformationMessage = string.Empty;
        NotifyCommands();
    }

    [RelayCommand]
    private async Task ShowPasswordHintAsync()
    {
        ErrorMessage = string.Empty;
        HintMessage = string.Empty;
        IsHintVisible = false;

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Ingrese su usuario para ver la pista.";
            return;
        }

        try
        {
            var hint = await _authService.GetPasswordHintAsync(Username);
            if (!string.IsNullOrEmpty(hint))
            {
                HintMessage = $"Pista: {hint}";
                IsHintVisible = true;
            }
            else
            {
                ErrorMessage = "El usuario no existe o no tiene pista configurada.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching password hint for user {UserReference}", Ticketfy.Logging.LogSanitizer.Pseudonymize(Username));
            ErrorMessage = "Error al consultar pista de contraseña.";
        }
    }
}
