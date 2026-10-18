namespace Ticketfy.Services.Interfaces;

public interface ILicenseEnforcementService
{
    bool IsSystemLocked();
    Ticketfy.Services.Security.LicenseEvaluation Evaluate();
    void InstallRenewal(string token);
}
