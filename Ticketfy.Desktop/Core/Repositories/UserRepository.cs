using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Core.Models;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Security;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services;
using Ticketfy.Services.Implementations;
using Ticketfy.Core.Security;

namespace Ticketfy.Core.Repositories;

public class UserRepository : IUserRepository
{
    private readonly CredentialVerifier _credentials;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    private readonly IUserRolePermissionEngine _permissionEngine;
    private readonly IAuditService? _auditService;

    public UserRepository(AppDbContext context, IUserRolePermissionEngine? permissionEngine = null, IAuditService? auditService = null,
        Func<string>? auditActor = null, CommercialOperationContext? operations = null, TimeProvider? clock = null, QuickAccessSession? quickAccess = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(context.Options), permissionEngine, auditService, auditActor, operations, clock, quickAccess) { }

    public UserRepository(IDbContextFactory<AppDbContext> factory, IUserRolePermissionEngine? permissionEngine = null, IAuditService? auditService = null,
        Func<string>? auditActor = null, CommercialOperationContext? operations = null, TimeProvider? clock = null, QuickAccessSession? quickAccess = null)
    {
        _factory = factory;
        _operations = operations;
        _permissionEngine = permissionEngine ?? new UserRolePermissionEngine();
        _auditService = auditService;
        _credentials = new(_factory, clock, quickAccess);
    }

    public async Task<List<UserModel>> GetActiveUsersAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var entities = await _context.Users
            .Where(u => u.IsActive)
            .AsNoTracking()
            .Select(u => new UserEntity
            {
                Id = u.Id,
                FullName = u.FullName,
                Username = u.Username,
                Role = u.Role,
                RoleString = u.RoleString,
                IsActive = u.IsActive
            })
            .ToListAsync();

        return entities.Select(MapToModel).ToList();
    }

    public async Task<UserModel?> ValidatePinAsync(string username, string pin4Digits)
    {
        var entity = await _credentials.ByUsernameAsync(username, pin4Digits, pin: true);
        return entity is null ? null : MapToModel(entity);
    }
    public async Task<UserModel?> VerifyPasswordAsync(string username, string password, bool supervisorOnly = false, Func<bool>? isRequestCurrent = null)
    {
        var entity = await _credentials.VerifyPasswordAsync(username, password, supervisorOnly, isRequestCurrent);
        return entity is null ? null : MapToModel(entity);
    }

    public async Task<bool> HasAnyUsersAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        return await _context.Users.AnyAsync();
    }

    public async Task<UserModel> CreateUserAsync(UserAccountDraft draft)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var user = Profile(draft);
        var actor = await AuthorizeUserChangeAsync(_context, user);
        await Ticketfy.Services.Implementations.InitialSetupStateStore.BeginIfEmptyAsync(_context);
        await UserAccountPolicy.RequireAssignableRoleAsync(_context, UserAccountPolicy.EffectiveRole(user));
        SetCredentials(user, draft);
        if (_auditService != null) await LogUserChangeAsync(actor, user.Id, AuditActionType.UserCreated, RiskLevel.Warning);
        _context.Users.Add(user);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "USER_CREATED", user.Id.ToString(), 0, "Alta de usuario");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        return MapToModel(user);
    }

    public async Task<UserModel> SaveOrUpdateAdminUserAsync(UserAccountDraft draft)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        await using var tx = await _context.Database.BeginTransactionAsync();
        var user = Profile(draft);
        if (UserAccountPolicy.EffectiveRole(user) != "ADMINISTRADOR" || !user.IsActive)
            throw new InvalidOperationException("La configuración inicial requiere un administrador activo.");
        var actor = await AuthorizeUserChangeAsync(_context, user);
        await Ticketfy.Services.Implementations.InitialSetupStateStore.BeginIfEmptyAsync(_context);
        var existing = await _context.Users.SingleOrDefaultAsync(u => u.Id == user.Id);
        if (await _context.Users.AnyAsync() && (existing is null || UserAccountPolicy.EffectiveRole(existing) != "ADMINISTRADOR"))
            throw new InvalidOperationException("Seleccione explícitamente la cuenta administradora que se va a actualizar.");
        SetCredentials(user, draft);
        if (_auditService != null)
            await LogUserChangeAsync(actor, user.Id, existing == null ? AuditActionType.UserCreated : AuditActionType.UserUpdated, RiskLevel.Critical);
        if (existing != null)
        {
            user.Id = existing.Id;
            existing.FullName = user.FullName;
            existing.Username = user.Username;
            existing.PasswordHash = user.PasswordHash;
            existing.PasswordPolicyVersion = user.PasswordPolicyVersion;
            existing.PasswordHint = string.Empty;
            existing.PinCode = user.PinCode;
            existing.Role = user.Role;
            existing.RoleString = user.RoleString;
            existing.IsActive = true;
            _context.Users.Update(existing);
        }
        else
        {
            _context.Users.Add(user);
        }
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "ADMIN_SAVED", user.Id.ToString(), 0, "Configuración administrativa");
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        return MapToModel(user);
    }

    public async Task<Ticketfy.Data.Dtos.UserDto?> GetAdminProfileAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        if (!await _context.Users.AnyAsync()) return null;
        await (_operations ?? throw new UnauthorizedAccessException("Sesión administrativa requerida."))
            .RequireAsync(_context, PermissionCatalog.UsersManage, false);
        return await UserAccountPolicy.ActiveAdministrators(_context).AsNoTracking()
            .OrderBy(u => u.Id).Select(u => new Ticketfy.Data.Dtos.UserDto(u.Id.ToString(), u.Username, u.FullName,
                u.RoleString ?? "ADMINISTRADOR", u.IsActive)).FirstOrDefaultAsync();
    }

    private async Task<OperationIdentity> AuthorizeUserChangeAsync(AppDbContext db, UserEntity user)
    {
        if (!await db.Users.AnyAsync())
        {
            if (UserAccountPolicy.EffectiveRole(user) != "ADMINISTRADOR" || !user.IsActive)
                throw new InvalidOperationException("La primera cuenta debe ser un administrador protegido.");
            return new(user.Id.ToString(), "bootstrap:local");
        }
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Sesión administrativa requerida."))
            .RequireAsync(db, PermissionCatalog.UsersManage, false);
        if (UserAccountPolicy.RequiresRoleManagement(user)) await _operations.RequireAsync(db, PermissionCatalog.UsersRoles, false);
        if (await AccountInputPolicy.Named(db, user.Username).AnyAsync(u => u.Id != user.Id))
            throw new InvalidOperationException("El usuario ya existe.");
        return actor;
    }

    private UserModel MapToModel(UserEntity entity)
    {
        return _permissionEngine.MapToModel(entity);
    }

    private static UserEntity Profile(UserAccountDraft draft)
    {
        if (draft.Id == Guid.Empty) throw new InvalidOperationException("La cuenta necesita una identidad válida.");
        string role = UserAccountPolicy.NormalizeRole(draft.Role);
        return new UserEntity
        {
            Id = draft.Id,
            FullName = AccountInputPolicy.FullName(draft.FullName),
            Username = AccountInputPolicy.Username(draft.Username),
            Role = UserAccountPolicy.StorageRole(role),
            RoleString = role,
            IsActive = draft.IsActive
        };
    }
    private static void SetCredentials(UserEntity user, UserAccountDraft draft)
    {
        user.PasswordHash = AccountInputPolicy.PasswordHash(draft.Password, user.Username);
        user.PasswordPolicyVersion = PasswordPolicy.CurrentVersion;
        user.PinCode = AccountInputPolicy.PinHash(draft.Pin);
    }

    private Task LogUserChangeAsync(OperationIdentity actor, Guid id, AuditActionType action, RiskLevel risk) => _auditService!.LogAsync(new AuditLogEntity
    {
        UserId = actor.UserId,
        EntityName = "User",
        EntityId = id.ToString(),
        ActionType = action,
        RiskLevel = risk,
        Reason = "Administrative user account change"
    });
}
