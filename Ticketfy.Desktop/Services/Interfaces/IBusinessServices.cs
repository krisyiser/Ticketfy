using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

/// <summary>Promotion rule CRUD and activation toggle.</summary>
public interface IPromotionService
{
    Task<List<PromotionDto>> GetAllAsync();
    Task<List<PromotionDto>> GetActiveAsync();
    Task SaveAsync(PromotionDto promotion);
    Task DeleteAsync(string id);
    Task<List<CartItemDto>> EvaluateAndApplyPromotionsAsync(List<CartItemDto> cartItems);
}

/// <summary>Parked/paused order CRUD.</summary>
public interface IParkedOrderService
{
    Task<List<ParkedOrderDto>> GetAllAsync();
    Task SaveAsync(ParkedOrderDto order);
    Task DeleteAsync(string id);
}

/// <summary>System user CRUD with RBAC.</summary>
public interface IUserService
{
    Task<int> GetCountAsync();
    Task<List<UserDto>> GetAllAsync();
    Task<UserDto?> GetByNameAsync(string name);
    Task<List<UserDto>> GetManagersAsync();
    Task SaveAsync(string id, string nombre, string username, string rol, string? passwordHash, string? pinHash, string? passwordHint = null);
    Task DeleteAsync(string id);
    Task<string?> GetPasswordHashAsync(string userId);
    Task<string?> GetPinHashAsync(string userId);
    Task UpdatePasswordHashAsync(string userId, string passwordHash);
    Task UpdatePinHashAsync(string userId, string pinHash);
    Task<string?> GetPasswordHintAsync(string username);
    async Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false)
    {
        string? hash = pin ? await GetPinHashAsync(userId) : await GetPasswordHashAsync(userId);
        return hash is not null && (pin ? Ticketfy.Services.Security.SecurityManager.VerifyPin(secret, hash)
            : Ticketfy.Services.Security.SecurityManager.VerifyPassword(secret, hash));
    }
}

/// <summary>Key-value settings persistence.</summary>
public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<Dictionary<string, string>> GetAllAsync();
    Task<Ticketfy.Core.Models.Settings.AppSettings> GetAppSettingsAsync();
    Task SaveAppSettingsAsync(Ticketfy.Core.Models.Settings.AppSettings settings);
}

/// <summary>System audit log writer for tamper-evident security tracking.</summary>
public interface IAuditService
{
    Task LogAsync(Ticketfy.Data.Entities.AuditLogEntity log);
    Task LogAsync(string level, string message, string? meta = null);
    Task<List<Ticketfy.Data.Entities.AuditLogEntity>> GetRecentLogsAsync(int limit = 100);
    Task<bool> VerifyIntegrityAsync();
}

/// <summary>Product co-occurrence engine for combo recommendations.</summary>
public interface IComboEngine
{
    Task UpdateCoOccurrencesAsync(List<string> productIds);
    Task<List<string>> GetRecommendationsAsync(string productId, int limit = 3);
}
