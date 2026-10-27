namespace Ticketfy.Services;

/// <summary>A draft cannot silently overwrite a rule changed since it was read.</summary>
public sealed class PromotionConflictException : InvalidOperationException
{
    public PromotionConflictException()
        : base("La promoción cambió o ya no existe. Sus cambios no se guardaron. Conserve lo que necesite del borrador, cierre y abra la versión actual antes de editar.") { }
}
