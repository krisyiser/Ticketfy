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

public record AuthenticateResult(bool IsSuccess, UserModel? User = null);

public sealed partial class AuthService : ObservableObject
{
    private readonly IUserService _userService;
    private readonly IUserRolePermissionEngine _permissionEngine;

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

    public async Task<AuthenticateResult> AuthenticateAsync(string username, string password)
    {
        var user = await _userService.GetByNameAsync(username);
        if (user is null || !user.IsActive)
        {
            Log.Warning("Authentication failed: user {UserReference} not found or inactive", LogSanitizer.Pseudonymize(username));
            return new AuthenticateResult(false);
        }

        var hash = await _userService.GetPasswordHashAsync(user.Id);
        if (hash is null || !SecurityManager.VerifyPassword(password, hash))
        {
            Log.Warning("Authentication failed: invalid credentials for user {UserReference}", LogSanitizer.Pseudonymize(username));
            return new AuthenticateResult(false);
        }

        if (SecurityManager.NeedsRehash(hash))
            await _userService.UpdatePasswordHashAsync(user.Id, SecurityManager.HashPassword(password));

        var userModel = _permissionEngine.MapDtoToModel(user);

        CurrentUser = user;
        IsAuthenticated = true;
        Log.Information("Login successful for user {UserReference} ({Role})", LogSanitizer.Pseudonymize(user.Username), userModel.RoleString);

        return new AuthenticateResult(true, userModel);
    }

    public async Task<string?> GetPasswordHintAsync(string username)
    {
        return await _userService.GetPasswordHintAsync(username);
    }

    public async Task<bool> VerifyManagerPinAsync(string userId, string pin)
    {
        var storedPin = await _userService.GetPinHashAsync(userId);
        if (storedPin is null || !SecurityManager.VerifyPin(pin, storedPin)) return false;
        if (SecurityManager.PinNeedsRehash(storedPin))
            await _userService.UpdatePinHashAsync(userId, SecurityManager.HashPin(pin));
        return true;
    }

    public async Task<bool> VerifyManagerPasswordAsync(string userId, string password)
    {
        var hash = await _userService.GetPasswordHashAsync(userId);
        if (hash is null || !SecurityManager.VerifyPassword(password, hash)) return false;
        if (SecurityManager.NeedsRehash(hash))
            await _userService.UpdatePasswordHashAsync(userId, SecurityManager.HashPassword(password));
        return true;
    }

    public void Logout()
    {
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
