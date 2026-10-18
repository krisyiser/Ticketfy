namespace Ticketfy.Core.Configuration;

/// <summary>
/// Central switchboard for integrations that are intentionally unavailable while
/// Ticketfy is developed and tested on this workstation.
///
/// Keep these values disabled until the server, download site and remote release
/// repository are formally returned to service. This is deliberately code-owned
/// configuration: environment variables cannot reactivate production traffic by
/// accident on a developer machine.
/// </summary>
public static class LocalDevelopmentMode
{
    public static bool IsEnabled => true;
    public static bool RemoteInfrastructureEnabled => false;
    public static bool FacturamaEnabled => false;

    public const string RemoteInfrastructureUnavailableMessage =
        "La infraestructura remota está deshabilitada durante el desarrollo local.";

    public const string FacturamaUnavailableMessage =
        "El módulo de Facturama está pausado y no puede emitir CFDI en esta etapa.";
}
