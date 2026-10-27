using System;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Core.Repositories;

namespace Ticketfy.Services.Security;

public class SecurityInterceptionService : ISecurityInterceptionService
{
    private readonly IUserRepository _userRepository;
    public event Action<string, Action<bool, UserModel?>>? RequestSupervisorPinDialog;

    public SecurityInterceptionService(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<Ticketfy.Core.Helpers.Result> ValidatePinAsync(string username, string inputPin)
    {
        // The repository shares durable attempt accounting with every credential entry point.
        var user = await _userRepository.ValidatePinAsync(username, inputPin);
        if (user != null)
            return Ticketfy.Core.Helpers.Result.Success();
        return Ticketfy.Core.Helpers.Result.Failure("Credencial incorrecta o temporalmente bloqueada. Si el PIN aún no está validado, inicie sesión con su contraseña.");
    }

    public async Task<(bool IsAuthorized, string? SupervisorId, string SignatureName)> AuthorizeHighRiskActionAsync(
        string actionTitle,
        string reasonRequiredMessage)
    {
        if (RequestSupervisorPinDialog == null)
        {
            return (false, null, string.Empty);
        }

        var tcs = new TaskCompletionSource<(bool, string?, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        RequestSupervisorPinDialog.Invoke(actionTitle, (authorized, user) =>
        {
            if (authorized && user is { IsActive: true, Role: SystemRole.ADMIN })
            {
                tcs.TrySetResult((true, user.Id.ToString(), user.FullName));
            }
            else
            {
                tcs.TrySetResult((false, null, string.Empty));
            }
        });

        return await tcs.Task;
    }
}
