using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Models;
using Ticketfy.Core.Services;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Threading.Tasks;
using Ticketfy.Services.Security;
using Ticketfy.Logging;

namespace Ticketfy.Services.Auth;

public record AuthenticateResult(bool IsSuccess, UserModel? User = null, bool PasswordChangeRequired = false);

public sealed partial class AuthService : ObservableObject
{
    private readonly IUserService _userService;
    private readonly IUserRolePermissionEngine _permissionEngine;
    private long _authenticationVersion;

    [ObservableProperty]
    private UserDto? _currentUser;

    [ObservableProperty]
    private bool _isAuthenticated;

    public AuthService(IUserService userService, IUserRolePermissionEngine? permissionEngine = null)
    {
        _userService = userService;
        _permissionEngine = permissionEngine ?? new UserRolePermissionEngine();
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        var result = await AuthenticateAsync(username, password);
        return result.IsSuccess;
    }

    public async Task<AuthenticateResult> AuthenticateAsync(string username, string password, Func<bool>? isRequestCurrent = null)
    {
        var version = Interlocked.Increment(ref _authenticationVersion);
        _userService.ClearQuickAccess();
        CurrentUser = null;
        IsAuthenticated = false;
        var result = await _userService.SignInAsync(username, password,
            () => version == _authenticationVersion && isRequestCurrent?.Invoke() != false);
        if (version != _authenticationVersion || isRequestCurrent?.Invoke() == false) return new(false);
        if (result.PasswordChangeRequired) return new(false, PasswordChangeRequired: true);
        var user = result.User;
        if (user is null)
        {
            Log.Warning("Authentication failed: invalid credentials for user {UserReference}", LogSanitizer.Pseudonymize(username));
            return new AuthenticateResult(false);
        }

        var userModel = _permissionEngine.MapDtoToModel(user);

        if (version != _authenticationVersion || isRequestCurrent?.Invoke() == false) return new AuthenticateResult(false);

        CurrentUser = user;
        IsAuthenticated = true;
        Log.Information("Login successful for user {UserReference} ({Role})", LogSanitizer.Pseudonymize(user.Username), userModel.RoleString);

        return new AuthenticateResult(true, userModel);
    }

    public async Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword)
    {
        var version = Interlocked.Increment(ref _authenticationVersion);
        _userService.ClearQuickAccess();
        CurrentUser = null;
        IsAuthenticated = false;
        bool changed = await _userService.UpgradeOwnPasswordAsync(username, currentPassword, newPassword);
        return version == _authenticationVersion && changed;
    }

    public async Task<string?> GetPasswordHintAsync(string username)
    {
        return await _userService.GetPasswordHintAsync(username);
    }

    public async Task<bool> VerifyManagerPinAsync(string userId, string pin)
    {
        return await _userService.VerifyManagerCredentialAsync(userId, pin, true);
    }

    public async Task<bool> VerifyManagerPasswordAsync(string userId, string password)
    {
        return await _userService.VerifyManagerCredentialAsync(userId, password);
    }

    public void Logout()
    {
        Interlocked.Increment(ref _authenticationVersion);
        _userService.ClearQuickAccess();
        Log.Information("Logout for user {UserReference}", LogSanitizer.Pseudonymize(CurrentUser?.Username));
        CurrentUser = null;
        IsAuthenticated = false;
    }

    public bool HasRole(string requiredRole)
    {
        if (CurrentUser is null) return false;

        string normReq = _permissionEngine.NormalizeRoleName(requiredRole);
        string normUser = _permissionEngine.NormalizeRoleName(CurrentUser.Rol);

        if (normReq == "CAJERO") return true;
        if (normReq == "GERENTE") return normUser is "GERENTE" or "ADMINISTRADOR";
        if (normReq == "ADMIN") return normUser is "ADMINISTRADOR";
        return string.Equals(normUser, normReq, StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> HasPermissionAsync(string permissionKey) =>
        CurrentUser is null
            ? Task.FromResult(false)
            : _permissionEngine.HasPermissionAsync(CurrentUser.Rol, permissionKey);
}
