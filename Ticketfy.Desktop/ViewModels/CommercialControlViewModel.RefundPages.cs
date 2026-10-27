using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;

namespace Ticketfy.ViewModels;

public partial class CommercialControlViewModel
{
    private sealed class RefundNavigation
    {
        public List<RefundQueueCursor?> Positions { get; } = [null];
        public RefundQueueCursor? Next { get; set; }
        public int Page { get; set; }
        public int Version { get; set; }
    }
    private readonly RefundNavigation _creditRefundNavigation = new(), _bankRefundNavigation = new();
    [ObservableProperty] private string _creditRefundSearch = "";
    [ObservableProperty] private string _bankRefundSearch = "";
    [ObservableProperty] private string _creditRefundPageLabel = "";
    [ObservableProperty] private string _bankRefundPageLabel = "";
    [ObservableProperty] private bool _hasNextCreditRefunds;
    [ObservableProperty] private bool _hasPreviousCreditRefunds;
    [ObservableProperty] private bool _hasNextBankRefunds;
    [ObservableProperty] private bool _hasPreviousBankRefunds;
    private bool CanResolveSelectedCreditRefund => PendingCreditRefund is { RequiresReview: false };
    private bool CanSettleSelectedRefund => Refund is { RequiresReview: false };
    partial void OnCreditRefundSearchChanged(string value) => ResetRefundQuery(true);
    partial void OnBankRefundSearchChanged(string value) => ResetRefundQuery(false);
    partial void OnRefundChanged(BankRefundHeader? value)
    { ExternalRefundVerified = false; Reference = ""; SettleRefundCommand.NotifyCanExecuteChanged(); }
    partial void OnReferenceChanged(string value)
    { ExternalRefundVerified = CreditRefundFundsVerified = CreditRefundNotExecutedVerified = false; }

    private void ClearRefundRows(bool credit)
    {
        if (credit)
        {
            PendingCreditRefunds.Clear(); PendingCreditRefund = null;
            CreditRefundFundsVerified = CreditRefundNotExecutedVerified = false;
        }
        else { PendingRefunds.Clear(); Refund = null; ExternalRefundVerified = false; }
        Reference = "";
    }
    private void RefundPageState(bool credit, string label, bool next = false, bool previous = false)
    {
        if (credit) { CreditRefundPageLabel = label; HasNextCreditRefunds = next; HasPreviousCreditRefunds = previous; }
        else { BankRefundPageLabel = label; HasNextBankRefunds = next; HasPreviousBankRefunds = previous; }
    }
    private void ResetRefundQuery(bool credit)
    {
        var state = credit ? _creditRefundNavigation : _bankRefundNavigation;
        ++state.Version; state.Page = 0; state.Next = null;
        state.Positions.Clear(); state.Positions.Add(null);
        ClearRefundRows(credit); RefundPageState(credit, "");
    }
    [RelayCommand] private Task SearchBankRefundsAsync() => SearchRefundsAsync(false);
    [RelayCommand] private Task SearchCreditRefundsAsync() => SearchRefundsAsync(true);
    private Task SearchRefundsAsync(bool credit) => RunAsync(async () =>
    { ResetRefundQuery(credit); await LoadRefundPageAsync(credit, null, 0); });
    [RelayCommand] private Task NextBankRefundsAsync() => MoveRefundPageAsync(false, true);
    [RelayCommand] private Task PreviousBankRefundsAsync() => MoveRefundPageAsync(false, false);
    [RelayCommand] private Task NextCreditRefundsAsync() => MoveRefundPageAsync(true, true);
    [RelayCommand] private Task PreviousCreditRefundsAsync() => MoveRefundPageAsync(true, false);
    private Task MoveRefundPageAsync(bool credit, bool next) => RunAsync(() =>
    {
        var state = credit ? _creditRefundNavigation : _bankRefundNavigation;
        if (next ? state.Next is null : state.Page == 0) return Task.CompletedTask;
        return LoadRefundPageAsync(credit, next ? state.Next : state.Positions[state.Page - 1], state.Page + (next ? 1 : -1));
    });

    private async Task LoadRefundPageAsync(bool credit, RefundQueueCursor? cursor, int page)
    {
        var state = credit ? _creditRefundNavigation : _bankRefundNavigation;
        int version = ++state.Version;
        ClearRefundRows(credit); RefundPageState(credit, "Consultando reembolsos…");
        try
        {
            RefundQueueCursor? next; long count;
            if (credit)
            {
                var result = await Credits.GetPendingRefundsPageAsync(search: CreditRefundSearch, after: cursor);
                if (state.Version != version) return;
                foreach (var item in result.Items) PendingCreditRefunds.Add(item);
                next = result.Next; count = result.TotalCount;
            }
            else
            {
                var result = await _reconciliation.GetPendingRefundsPageAsync(search: BankRefundSearch, after: cursor);
                if (state.Version != version) return;
                foreach (var item in result.Items) PendingRefunds.Add(item);
                next = result.Next; count = result.TotalCount;
            }
            state.Page = page; state.Next = next;
            if (state.Positions.Count > page) state.Positions.RemoveRange(page, state.Positions.Count - page);
            state.Positions.Add(cursor);
            RefundPageState(credit, $"Página {page + 1} · {count:N0} pendientes en la búsqueda · " +
                (credit ? "Caja actual" : "Todas las cajas") + " · orden por referencia local", next is not null, page > 0);
        }
        catch (Exception ex)
        {
            if (state.Version != version) return;
            ClearRefundRows(credit);
            RefundPageState(credit, "Consulta no disponible. No significa que no haya reembolsos; vuelva a buscar.");
            throw new InvalidOperationException("No se pudieron consultar los reembolsos. Vuelva a buscar con su sesión actual.", ex);
        }
    }

    private async Task<bool> RefreshRefundsAfterCommitAsync(bool credit, string success)
    {
        var state = credit ? _creditRefundNavigation : _bankRefundNavigation;
        int expectedVersion = state.Version + 1;
        Message = success; Summary = "";
        try { await LoadRefundPageAsync(credit, state.Positions[state.Page], state.Page); }
        catch { Summary = success + " No se pudo actualizar la lista. Consulte la misma referencia; no vuelva a mover dinero."; }
        return state.Version == expectedVersion;
    }
}
