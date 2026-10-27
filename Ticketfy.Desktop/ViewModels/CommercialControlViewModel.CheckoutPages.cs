using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;

namespace Ticketfy.ViewModels;

public partial class CommercialControlViewModel
{
    private readonly List<PendingCheckoutCursor?> _pendingCheckoutPositions = [null];
    private PendingCheckoutCursor? _pendingCheckoutNext;
    private int _pendingCheckoutPage;
    private int _pendingCheckoutVersion;
    [ObservableProperty] private string _pendingCheckoutSearch = "";
    [ObservableProperty] private string _pendingCheckoutPageLabel = "";
    [ObservableProperty] private bool _hasNextPendingCheckouts;
    [ObservableProperty] private bool _hasPreviousPendingCheckouts;

    partial void OnPendingCheckoutSearchChanged(string value) => ResetPendingCheckoutQuery();

    private void ResetPendingCheckoutQuery()
    {
        ++_pendingCheckoutVersion;
        _pendingCheckoutPage = 0; _pendingCheckoutNext = null;
        _pendingCheckoutPositions.Clear(); _pendingCheckoutPositions.Add(null);
        HasNextPendingCheckouts = HasPreviousPendingCheckouts = false;
        PendingCheckouts.Clear(); PendingCheckout = null;
        CheckoutBankStatus = ""; CheckoutProviderOrderId = ""; CheckoutFundsVerified = false;
        PendingCheckoutPageLabel = "";
    }

    [RelayCommand]
    private Task SearchPendingCheckoutsAsync() => RunAsync(async () =>
    {
        ResetPendingCheckoutQuery();
        await LoadPendingCheckoutPageAsync(null, 0);
    });
    [RelayCommand]
    private Task NextPendingCheckoutsAsync() => RunAsync(() => HasNextPendingCheckouts && _pendingCheckoutNext is not null
        ? LoadPendingCheckoutPageAsync(_pendingCheckoutNext, _pendingCheckoutPage + 1) : Task.CompletedTask);
    [RelayCommand]
    private Task PreviousPendingCheckoutsAsync() => RunAsync(() => HasPreviousPendingCheckouts
        ? LoadPendingCheckoutPageAsync(_pendingCheckoutPositions[_pendingCheckoutPage - 1], _pendingCheckoutPage - 1) : Task.CompletedTask);

    private async Task LoadPendingCheckoutPageAsync(PendingCheckoutCursor? cursor, int page)
    {
        int version = ++_pendingCheckoutVersion;
        PendingCheckouts.Clear(); PendingCheckout = null;
        CheckoutFundsVerified = false; CheckoutBankStatus = "";
        HasNextPendingCheckouts = HasPreviousPendingCheckouts = false;
        PendingCheckoutPageLabel = "Consultando pendientes…";
        try
        {
            if (_sales is null) throw new InvalidOperationException("Consulta de cobros no disponible.");
            var result = await _sales.GetInterruptedCheckoutsPageAsync(search: PendingCheckoutSearch, after: cursor);
            if (version != _pendingCheckoutVersion) return;
            _pendingCheckoutPage = page; _pendingCheckoutNext = result.Next;
            if (_pendingCheckoutPositions.Count > page) _pendingCheckoutPositions.RemoveRange(page, _pendingCheckoutPositions.Count - page);
            _pendingCheckoutPositions.Add(cursor);
            foreach (var item in result.Items) PendingCheckouts.Add(item);
            HasNextPendingCheckouts = result.Next is not null;
            HasPreviousPendingCheckouts = page > 0;
            PendingCheckoutPageLabel = $"Página {page + 1} · {result.TotalCount:N0} pendientes en la búsqueda · orden por referencia";
        }
        catch (Exception ex)
        {
            if (version != _pendingCheckoutVersion) return;
            PendingCheckouts.Clear(); PendingCheckout = null;
            PendingCheckoutPageLabel = "Consulta no disponible. Busque de nuevo; no significa que no haya pendientes.";
            throw new InvalidOperationException("No se pudieron consultar los cobros pendientes. Vuelva a consultar con su sesión actual.", ex);
        }
    }
}
