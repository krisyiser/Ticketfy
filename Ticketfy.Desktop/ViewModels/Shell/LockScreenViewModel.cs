using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Repositories;
using Ticketfy.Core.Services;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Shell;

/// <summary>
/// Manages terminal lock state, primary/short-lived PIN unlock, and idle auto-lock trigger.
/// Fully decoupled from navigation and dialog concerns.
/// </summary>
public partial class LockScreenViewModel : ObservableObject, System.IDisposable
{
    private readonly IUserRepository _userRepository;
    private readonly ISessionManager _sessionManager;
    private long _requestVersion;
    private bool _dismissed;
    public bool LastUnlockSucceeded { get; private set; }

    [ObservableProperty] private bool _isLocked = false;
    [ObservableProperty] private string _unlockPin = string.Empty;
    [ObservableProperty] private string _unlockErrorMessage = string.Empty;
    [ObservableProperty] private bool _usePassword = true;
    partial void OnUsePasswordChanged(bool value) { ++_requestVersion; UnlockPin = ""; UnlockErrorMessage = ""; }

    public LockScreenViewModel(IUserRepository userRepository, ISessionManager sessionManager)
    {
        _userRepository = userRepository;
        _sessionManager = sessionManager;

        _sessionManager.LockStateChanged += OnLockStateChanged;
        IsLocked = _sessionManager.IsTerminalLocked;
    }
    private void OnLockStateChanged(bool locked) { ++_requestVersion; UnlockPin = ""; IsLocked = locked; }
    public void Dispose()
    {
        _dismissed = true; ++_requestVersion; UnlockPin = ""; LastUnlockSucceeded = false;
        _sessionManager.LockStateChanged -= OnLockStateChanged;
    }

    [RelayCommand]
    private async Task UnlockTerminalAsync()
    {
        LastUnlockSucceeded = false;
        if (_dismissed) return;
        UnlockErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(UnlockPin))
        {
            UnlockErrorMessage = "La credencial es obligatoria.";
            return;
        }

        var current = _sessionManager.CurrentCashier;
        var version = _sessionManager.SessionVersion;
        var requestVersion = _requestVersion;
        var pin = UnlockPin;
        bool password = UsePassword;
        UnlockPin = string.Empty;
        if (current is null || !_sessionManager.IsTerminalLocked)
        {
            UnlockErrorMessage = "Inicie sesión para continuar.";
            return;
        }
        try
        {
            var user = password
                ? await _userRepository.VerifyPasswordAsync(current.Username, pin,
                    isRequestCurrent: () => !_dismissed && requestVersion == _requestVersion && _sessionManager.SessionVersion == version && _sessionManager.CurrentCashier?.Id == current.Id && _sessionManager.IsTerminalLocked)
                : await _userRepository.ValidatePinAsync(current.Username, pin);
            if (_dismissed || requestVersion != _requestVersion || _sessionManager.SessionVersion != version || _sessionManager.CurrentCashier?.Id != current.Id) return;
            if (user?.Id == current.Id && user.IsActive)
            {
                _sessionManager.UnlockTerminal();
                LastUnlockSucceeded = true;
            }
            else
            {
                if (!password) UsePassword = true;
                UnlockErrorMessage = "Acceso no verificado. Use su contraseña; el PIN requiere validarla en los últimos 15 minutos de esta ejecución. Si debe actualizarla, cierre sesión y use el acceso principal.";
            }
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Warning(ex, "No se pudo validar el desbloqueo de sesión");
            if (!_dismissed && requestVersion == _requestVersion && _sessionManager.SessionVersion == version)
                UnlockErrorMessage = "No se pudo validar la credencial. La sesión continúa bloqueada.";
        }
    }

    /// <summary>
    /// Triggers auto-lock when idle timeout fires from MainWindow.
    /// </summary>
    public void TriggerAutoLock()
    {
        if (_sessionManager.CurrentCashier != null && !IsLocked)
        {
            _sessionManager.LockTerminal();
        }
    }
}
