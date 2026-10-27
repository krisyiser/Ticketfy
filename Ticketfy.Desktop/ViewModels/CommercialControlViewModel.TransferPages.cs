using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;

namespace Ticketfy.ViewModels;

public partial class CommercialControlViewModel
{
    private readonly List<TransferQueueCursor?> _transferPositions = [null];
    private TransferQueueCursor? _transferNext;
    private int _transferPage;
    private int _transferQueryVersion;
    [ObservableProperty] private string _transferSearch = "";
    [ObservableProperty] private string _transferPageLabel = "";
    [ObservableProperty] private bool _transferHistoryMode;
    [ObservableProperty] private bool _hasNextTransfers;
    [ObservableProperty] private bool _hasPreviousTransfers;

    partial void OnTransferSearchChanged(string value) => ResetTransferQuery();

    private void ResetTransferQuery()
    {
        ++_transferQueryVersion;
        _transferPage = 0; _transferNext = null;
        _transferPositions.Clear(); _transferPositions.Add(null);
        PendingTransfers.Clear(); PendingTransfer = null;
        TransferReference = TransferRefundReference = "";
        TransferFundsVerified = TransferNoFundsVerified = TransferRefundVerified = false;
        TransferPageLabel = ""; HasNextTransfers = HasPreviousTransfers = false;
        Rows.Clear();
    }

    private async Task LoadPendingTransfersAsync()
    {
        TransferHistoryMode = false; ResetTransferQuery();
        await LoadTransferPageAsync(null, 0);
    }

    [RelayCommand] private Task PendingTransfersAsync() => RunAsync(LoadPendingTransfersAsync);
    [RelayCommand]
    private Task SearchTransfersAsync() => RunAsync(async () =>
    { ResetTransferQuery(); await LoadTransferPageAsync(null, 0); });
    [RelayCommand]
    private Task NextTransfersAsync() => RunAsync(() => HasNextTransfers && _transferNext is not null
        ? LoadTransferPageAsync(_transferNext, _transferPage + 1) : Task.CompletedTask);
    [RelayCommand]
    private Task PreviousTransfersAsync() => RunAsync(() => HasPreviousTransfers
        ? LoadTransferPageAsync(_transferPositions[_transferPage - 1], _transferPage - 1) : Task.CompletedTask);

    private async Task LoadTransferPageAsync(TransferQueueCursor? cursor, int page)
    {
        int version = ++_transferQueryVersion;
        PendingTransfers.Clear(); PendingTransfer = null; Rows.Clear();
        TransferReference = TransferRefundReference = "";
        TransferFundsVerified = TransferNoFundsVerified = TransferRefundVerified = false;
        HasNextTransfers = HasPreviousTransfers = false;
        TransferPageLabel = "Consultando transferencias…";
        try
        {
            TransferQueueCursor? next; long count;
            if (TransferHistoryMode)
            {
                var result = await Transfers.GetReceiptsPageAsync(search: TransferSearch, after: cursor);
                if (version != _transferQueryVersion) return;
                foreach (var r in result.Items)
                    Rows.Add(new(r.CreatedAtUtc, r.DocumentId, r.OperationId + " · " + r.Purpose + " · " + r.Outcome +
                        " · Depósito: " + r.ExternalReference + (r.RefundReference is null ? "" : " · Reembolso: " + r.RefundReference),
                        r.Amount, 0, r.CashierUserId, r.RegisterId, r.ShiftId));
                next = result.Next; count = result.TotalCount;
                Summary = "Historial de todas las cajas autorizadas por el permiso de reportes. No son ventas adicionales; concilie con el estado bancario.";
            }
            else
            {
                var result = await Transfers.GetPendingPageAsync(search: TransferSearch, after: cursor);
                if (version != _transferQueryVersion) return;
                foreach (var item in result.Items) PendingTransfers.Add(item);
                next = result.Next; count = result.TotalCount;
            }
            _transferPage = page; _transferNext = next;
            if (_transferPositions.Count > page) _transferPositions.RemoveRange(page, _transferPositions.Count - page);
            _transferPositions.Add(cursor);
            HasNextTransfers = next is not null; HasPreviousTransfers = page > 0;
            TransferPageLabel = $"{(TransferHistoryMode ? "Historial" : "Pendientes")} · Página {page + 1} · {count:N0} registros en la búsqueda · orden por referencia local";
        }
        catch (Exception ex)
        {
            if (version != _transferQueryVersion) return;
            PendingTransfers.Clear(); PendingTransfer = null; Rows.Clear();
            TransferPageLabel = "Consulta no disponible. No significa que no haya transferencias; vuelva a buscar.";
            throw new InvalidOperationException("No se pudieron consultar las transferencias. Vuelva a consultar con su sesión actual.", ex);
        }
    }

    private Task ExecuteSelectedTransferAsync(Func<PendingTransferDto, Task> execute, string success) => RunAsync(async () =>
    {
        var selected = PendingTransfer;
        if (TransferHistoryMode || selected is null || !PendingTransfers.Contains(selected))
            throw new InvalidOperationException("Consulte y seleccione un intento vigente de la lista.");
        int version = _transferQueryVersion;
        try { await execute(selected); }
        catch when (version != _transferQueryVersion) { return; }
        if (version != _transferQueryVersion) return;
        PendingTransfers.Remove(selected); PendingTransfer = null;
        Message = "Resolución registrada. No vuelva a mover dinero."; Summary = success;
        try { await LoadTransferPageAsync(_transferPositions[_transferPage], _transferPage); }
        catch { Summary = success + " No se pudo actualizar la lista. Consulte la misma referencia; no vuelva a mover dinero."; }
    });
}
