namespace Ticketfy.Services;

/// <summary>Only thrown before commit: the caller may safely correct the draft.</summary>
public class KitCommandRejectedException(string message) : InvalidOperationException(message);

public sealed class KitConflictException() : KitCommandRejectedException(
    "El combo cambió desde que abrió el formulario. Cierre y vuelva a editar la versión actual; su borrador no se guardó.");
