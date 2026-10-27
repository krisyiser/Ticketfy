using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Entities;

namespace Ticketfy.Core.Repositories;

public interface IUserRepository
{
    Task<List<UserModel>> GetActiveUsersAsync();
    Task<UserModel?> ValidatePinAsync(string username, string pin4Digits);
    Task<UserModel?> VerifyPasswordAsync(string username, string password, bool supervisorOnly = false, System.Func<bool>? isRequestCurrent = null);
    Task<bool> HasAnyUsersAsync();
    Task<UserModel> CreateUserAsync(UserAccountDraft user);
    Task<UserModel> SaveOrUpdateAdminUserAsync(UserAccountDraft user);
    Task<Ticketfy.Data.Dtos.UserDto?> GetAdminProfileAsync();
}
