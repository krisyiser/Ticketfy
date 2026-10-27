using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

/// <summary>Promotion rule CRUD and activation toggle.</summary>
public interface IPromotionService
{
    /// <summary>Compatibility reads reject more than 200 rows; never use for pricing.</summary>
    Task<List<PromotionDto>> GetAllAsync();
    Task<List<PromotionDto>> GetActiveAsync();
    Task<CatalogPage<PromotionDto>> GetPageAsync(int page, int size = 100, string? search = null);
    // Null revision means creation; edits carry the revision read from the directory.
    // Reuse the operation ID and the exact request when its result is uncertain.
    Task SaveAsync(PromotionDto promotion, string operationId);
    Task SetActiveAsync(string id, bool active, string operationId);
    Task DeleteAsync(string id, string operationId);
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
    Task<List<UserDto>> GetAllAsync();
    Task<UserDto?> AuthenticateAsync(string username, string password);
    Task<SignInResult> SignInAsync(string username, string password, Func<bool>? isRequestCurrent = null);
    void ClearQuickAccess();
    Task<bool> UpgradeOwnPasswordAsync(string username, string currentPassword, string newPassword);
    Task<bool> VerifyManagerCredentialAsync(string userId, string secret, bool pin = false);
    Task<bool> ConfirmCurrentAdministratorAsync(string password);
    // Credentials are literal secrets, never caller-supplied hashes. On edit null preserves a credential;
    // an empty PIN removes PIN access, and an empty password preserves the existing password.
    Task SaveAsync(string id, string nombre, string username, string rol, string? password, string? pin, string? passwordHint = null);
    Task DeleteAsync(string id);
    Task ChangePasswordAsync(string userId, string password);
    Task ChangePinAsync(string userId, string pin);
    Task<string?> GetPasswordHintAsync(string username);
    Task<bool> VerifyCredentialAsync(string userId, string secret, bool pin = false);
}

/// <summary>Key-value settings persistence.</summary>
public enum AppSettingsSection { Visual, Hardware, Security }
public interface ISettingsService
{
    Task<InitialSetupStage> GetInitialSetupStageAsync() => throw new NotSupportedException();
    Task<InitialBusinessProfile> GetInitialBusinessAsync() => throw new NotSupportedException();
    Task SaveInitialBusinessAsync(InitialBusinessProfile profile) => throw new NotSupportedException();
    Task CompleteInitialSetupAsync() => throw new NotSupportedException();
    Task SaveBusinessSettingsAsync(IReadOnlyDictionary<string, string> values) => throw new NotSupportedException();
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task AddCustomRoleAsync(string name) => throw new NotSupportedException();
    Task DeleteCustomRoleAsync(string name) => throw new NotSupportedException();
    Task<Dictionary<string, string>> GetAllAsync();
    Task<Ticketfy.Core.Models.Settings.AppSettings> GetAppSettingsAsync();
    Task SaveAppSettingsAsync(Ticketfy.Core.Models.Settings.AppSettings settings);
    async Task SaveSectionAsync(Ticketfy.Core.Models.Settings.AppSettings settings, AppSettingsSection section)
    {
        var current = await GetAppSettingsAsync();
        switch (section)
        {
            case AppSettingsSection.Visual: current.Visual = settings.Visual; break;
            case AppSettingsSection.Hardware: current.Hardware = settings.Hardware; current.Ticket = settings.Ticket; break;
            case AppSettingsSection.Security: current.System = settings.System; current.Company.TimeZoneId = settings.Company.TimeZoneId; break;
            default: throw new System.ArgumentOutOfRangeException(nameof(section));
        }
        await SaveAppSettingsAsync(current);
    }
}

/// <summary>System audit log writer for tamper-evident security tracking.</summary>
public interface IAuditService
{
    Task LogAsync(Ticketfy.Data.Entities.AuditLogEntity log);
    Task LogAsync(string level, string message, string? meta = null);
    Task<List<Ticketfy.Data.Entities.AuditLogEntity>> GetRecentLogsAsync(int limit = 100);
    Task<bool> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}

/// <summary>Product co-occurrence engine for combo recommendations.</summary>
public interface IComboEngine
{
    Task UpdateCoOccurrencesAsync(List<string> productIds);
    Task<List<string>> GetRecommendationsAsync(string productId, int limit = 3);
}
