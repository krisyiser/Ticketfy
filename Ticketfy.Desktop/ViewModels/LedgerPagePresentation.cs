using Ticketfy.Data.Dtos;

namespace Ticketfy.ViewModels;

internal static class LedgerPagePresentation
{
    internal static string AccountStatus(AccountStatementPageDto page)
    {
        string status = $"Saldo inicial de esta página: {page.OpeningBalance:C2}. El saldo de la consulta incluye todas sus páginas.";
        if (page.IncludesMigrationOpening) status += " Incluye apertura migrada; no reconstruye movimientos históricos ausentes.";
        if (page.HasUnreconciledHistory) status += " ATENCIÓN: histórico sin conciliar o diferencia con el saldo registrado. Revise Históricos antes de darlo por conciliado.";
        if (page.RecordedBalance is { } recorded && (page.HasUnreconciledHistory || page.HasNewerEntries))
            status += $" Saldo registrado actualmente: {recorded:C2}.";
        if (page.HasNewerEntries) status += " Hay movimientos nuevos fuera de esta consulta. Actualice para incluirlos.";
        return status;
    }
}
