using System;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;

namespace Ticketfy.Services.Security;

public class SecurityInterceptionService : ISecurityInterceptionService
{
    private readonly IUserRepository _userRepository;
    public event Action<string, Action<bool, UserModel?>>? RequestSupervisorPinDialog;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Attempts, DateTime LockoutEnd)> _failedAttempts = new();

    public SecurityInterceptionService(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<Ticketfy.Core.Helpers.Result> ValidatePinAsync(string username, string inputPin)
    {
        // 1. RATE LIMITING (Bloqueo en memoria)
        if (_failedAttempts.TryGetValue(username, out var record) && record.LockoutEnd > DateTime.Now)
        {
            return Ticketfy.Core.Helpers.Result.Failure($"Usuario bloqueado. Intente de nuevo en {(record.LockoutEnd - DateTime.Now).Seconds}s.");
        }

        var user = await _userRepository.ValidatePinAsync(username, inputPin);
        if (user != null)
        {
            _failedAttempts.TryRemove(username, out _);
            return Ticketfy.Core.Helpers.Result.Success();
        }

        // 3. CASTIGO EXPONENCIAL
        int attempts = _failedAttempts.TryGetValue(username, out var existing) ? existing.Attempts + 1 : 1;
        DateTime lockout = attempts >= 3 ? DateTime.Now.AddSeconds(30) : DateTime.MinValue;
        _failedAttempts[username] = (attempts, lockout);

        return Ticketfy.Core.Helpers.Result.Failure("PIN Incorrecto.");
    }

    public async Task<(bool IsAuthorized, string? SupervisorId, string SignatureName)> AuthorizeHighRiskActionAsync(
        string actionTitle,
        string reasonRequiredMessage)
    {
        if (RequestSupervisorPinDialog == null)
        {
            return (false, null, string.Empty);
        }

        var tcs = new TaskCompletionSource<(bool, string?, string)>();

        RequestSupervisorPinDialog.Invoke(actionTitle, (authorized, user) =>
        {
            if (authorized && user != null)
            {
                tcs.SetResult((true, user.Id.ToString(), user.FullName));
            }
            else
            {
                tcs.SetResult((false, null, string.Empty));
            }
        });

        return await tcs.Task;
    }
}
