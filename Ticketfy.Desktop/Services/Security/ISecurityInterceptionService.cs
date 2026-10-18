using System;
using System.Threading.Tasks;

namespace Ticketfy.Services.Security;

public interface ISecurityInterceptionService
{
    Task<(bool IsAuthorized, string? SupervisorId, string SignatureName)> AuthorizeHighRiskActionAsync(
        string actionTitle,
        string reasonRequiredMessage);

    Task<Ticketfy.Core.Helpers.Result> ValidatePinAsync(string username, string inputPin);
}
