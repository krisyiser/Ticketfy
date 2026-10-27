using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;

namespace Ticketfy.ViewModels;

public partial class CommercialControlViewModel
{
    private sealed record LedgerSelection(string Kind, string Id, string Name, string Unit);
    private LedgerSelection? _ledgerSelection;
    private LedgerReadCut? _ledgerCut;
    private int _ledgerPage;
    private int _ledgerRequestVersion;
    [ObservableProperty] private bool _ledgerVisible;
    [ObservableProperty] private bool _hasOlderLedger;
    [ObservableProperty] private bool _hasRecentLedger;
    [ObservableProperty] private string _ledgerPageLabel = "";

    partial void OnProductChanged(ProductDto? value) => ResetLedgerQuery();
    partial void OnWalletNumberChanged(string value) => ResetLedgerQuery();

    private void ResetLedgerQuery()
    {
        ++_ledgerRequestVersion;
        _ledgerSelection = null; _ledgerCut = null; _ledgerPage = 0;
        LedgerVisible = HasOlderLedger = HasRecentLedger = false;
        Rows.Clear(); Summary = ""; LedgerPageLabel = "";
    }
    private Task StartLedgerAsync(string kind, string id, string name, string unit = "")
    {
        ResetLedgerQuery();
        _ledgerSelection = new(kind, id, name, unit);
        return LoadLedgerPageAsync(0, null);
    }
    [RelayCommand]
    private Task OlderLedgerAsync() => RunAsync(
        () => HasOlderLedger ? LoadLedgerPageAsync(_ledgerPage + 1, _ledgerCut) : Task.CompletedTask, keepLedger: true);
    [RelayCommand]
    private Task RecentLedgerAsync() => RunAsync(
        () => HasRecentLedger ? LoadLedgerPageAsync(_ledgerPage - 1, _ledgerCut) : Task.CompletedTask, keepLedger: true);
    [RelayCommand] private Task RefreshLedgerAsync() => RunAsync(() => LoadLedgerPageAsync(0, null), keepLedger: true);

    private async Task LoadLedgerPageAsync(int page, LedgerReadCut? cut)
    {
        var selection = _ledgerSelection;
        if (selection is null) return;
        int version = ++_ledgerRequestVersion;
        Rows.Clear(); Summary = ""; HasOlderLedger = HasRecentLedger = false; LedgerPageLabel = "";
        try
        {
            LedgerReadCut returnedCut;
            bool more;
            long count;
            if (selection.Kind == "Inventory")
            {
                var result = await _products.GetKardexPageAsync(selection.Id, page, cut: cut);
                if (version != _ledgerRequestVersion) return;
                foreach (var m in result.Entries)
                    Rows.Add(new(m.CreatedAtUtc, m.DocumentId, m.Reason, m.Quantity, m.StockAfter, m.CashierUserId, m.RegisterId, m.ShiftId ?? "Migración"));
                Summary = $"Kardex de {selection.Name} · Unidad: {selection.Unit}. Existencia registrada actualmente: {result.RecordedStock:N3}. "
                    + (result.LedgerStock is { } stock ? $"Existencia al corte consultado: {stock:N3}." : "No hay un saldo de inventario respaldado por movimientos.")
                    + (result.HasUnreconciledHistory ? " ATENCIÓN: diferencia con el ledger o saldo inicial ausente; requiere revisión." : "")
                    + (result.IncludesMigrationOpening ? " Incluye apertura migrada; no reconstruye movimientos históricos ausentes." : "")
                    + (result.HasNewerEntries ? " Hay movimientos nuevos. Actualice para incluirlos." : "");
                returnedCut = result.Cut; more = result.HasMore; count = result.TotalCount;
            }
            else
            {
                var result = selection.Kind switch
                {
                    "Customer" => await _customers.GetStatementPageAsync(selection.Id, page, cut: cut),
                    "Supplier" => await _purchases.GetStatementPageAsync(selection.Id, page, cut: cut),
                    "Giftcard" => await _wallets.GetStatementPageAsync(selection.Id, page, cut: cut),
                    _ => throw new InvalidOperationException("Consulta de ledger no soportada.")
                };
                if (version != _ledgerRequestVersion) return;
                ShowStatement(result, selection.Name, selection.Kind == "Giftcard");
                returnedCut = result.Cut; more = result.HasMore; count = result.TotalCount;
            }
            _ledgerCut = returnedCut; _ledgerPage = page;
            HasOlderLedger = more; HasRecentLedger = page > 0; LedgerVisible = true;
            LedgerPageLabel = $"Página {page + 1} desde los más recientes · {count:N0} movimientos";
        }
        catch
        {
            if (version != _ledgerRequestVersion) return;
            Rows.Clear(); Summary = "No hay un estado de cuenta validado. Vuelva a consultar con su sesión actual.";
            LedgerVisible = false; _ledgerCut = null;
            throw;
        }
    }
}
