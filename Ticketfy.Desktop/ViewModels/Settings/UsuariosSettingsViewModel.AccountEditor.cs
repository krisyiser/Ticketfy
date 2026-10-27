using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Ticketfy.Data.Dtos;

namespace Ticketfy.ViewModels.Settings;

public partial class UsuariosSettingsViewModel
{
    private string _accountDraftId = Guid.NewGuid().ToString();
    private long _accountEditVersion;
    private bool _usersReadFailed;
    [ObservableProperty] private bool _isEditingUser;
    [ObservableProperty] private bool _isUserSaving;
    [ObservableProperty] private bool _disableEditedPin;
    public string UserEditorTitle => IsEditingUser ? "Editar cuenta seleccionada" : "Registrar usuario";
    public string CredentialInstructions => IsEditingUser
        ? "Contraseña y PIN vacíos conservan las credenciales. Una contraseña nueva requiere al menos 15 caracteres. Puede deshabilitar expresamente el PIN."
        : "Contraseña: frase de al menos 15 caracteres, hasta 1024 unidades de texto. PIN de cuatro dígitos. Usuario: hasta 128 caracteres; nombre: hasta 256.";
    partial void OnIsEditingUserChanged(bool value)
    { OnPropertyChanged(nameof(UserEditorTitle)); OnPropertyChanged(nameof(CredentialInstructions)); }

    private void ResetUserEditor()
    {
        ++_accountEditVersion;
        _accountDraftId = Guid.NewGuid().ToString();
        IsEditingUser = DisableEditedPin = false;
        NewUsername = NewFullName = NewPassword = NewPasswordHint = NewPin1 = NewPin2 = NewPin3 = NewPin4 = "";
        NewRole = "CAJERO";
    }

    [RelayCommand]
    private void EditUser(UserDto user)
    {
        if (IsUserSaving || !Users.Contains(user)) return;
        ResetUserEditor();
        IsEditingUser = true; _accountDraftId = user.Id;
        NewUsername = user.Username; NewFullName = user.FullName;
        if (!RoleOptions.Contains(user.Role)) RoleOptions.Add(user.Role); // Preserve legacy assignment, not a new catalog role.
        NewRole = user.Role;
        FeedbackMessage = user.IsActive ? "Editando esta cuenta; su identidad y registros históricos se conservan."
            : "Cuenta inactiva: editar sus datos no la reactiva.";
    }

    [RelayCommand]
    private void CancelUserEdit()
    {
        if (IsUserSaving) return;
        ResetUserEditor(); FeedbackMessage = "";
    }

    private async Task SaveUserCoreAsync()
    {
        if (_userService is null || IsUserSaving) return;
        long security = _securityVersion, edit = _accountEditVersion;
        bool editing = IsEditingUser;
        string enteredPin = $"{NewPin1}{NewPin2}{NewPin3}{NewPin4}";
        string? pin = editing && DisableEditedPin ? "" : editing && enteredPin.Length == 0 ? null : enteredPin;
        if (!(editing && (pin is null || DisableEditedPin)) && (pin is not { Length: 4 } || !pin.All(char.IsAsciiDigit)))
        { FeedbackMessage = "El PIN nuevo debe contener exactamente cuatro dígitos."; return; }
        IsUserSaving = true;
        try
        {
            await _userService.SaveAsync(_accountDraftId, NewFullName, NewUsername, NewRole, NewPassword, pin);
            if (security != _securityVersion || edit != _accountEditVersion) return;
            ResetUserEditor();
            await LoadAsync();
            if (security != _securityVersion) return;
            FeedbackMessage = editing ? "Cuenta actualizada; historial conservado." : "Usuario registrado.";
            if (_usersReadFailed) FeedbackMessage += " No se pudo actualizar la lista; vuelva a consultar, no repita el alta.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Administrative account save failed");
            if (security == _securityVersion && edit == _accountEditVersion)
                FeedbackMessage = ex is InvalidOperationException or ArgumentException ? ex.Message : "No se pudo guardar la cuenta. Verifique su sesión e intente de nuevo.";
        }
        finally { IsUserSaving = false; }
    }
}
