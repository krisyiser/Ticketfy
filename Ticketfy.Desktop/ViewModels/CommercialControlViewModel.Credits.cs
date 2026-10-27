using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Ticketfy.Core.Security;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels;

public partial class CommercialControlViewModel
{
    private AccountCreditSettlementService Credits => new(_factory, _operations);
    [ObservableProperty] private bool _canSettleCredit;
    [ObservableProperty] private bool _creditSupplier;
    [ObservableProperty] private AccountDocumentBalance? _creditSource;
    [ObservableProperty] private AccountDocumentBalance? _creditTarget;
    [ObservableProperty] private bool _creditActionVerified;
    [ObservableProperty] private string _creditExplanation = "Seleccione la cuenta y consulte sus documentos antes de continuar.";
    public ObservableCollection<AccountDocumentBalance> CreditSources { get; } = [];
    public ObservableCollection<AccountDocumentBalance> CreditTargets { get; } = [];
    public ObservableCollection<CreditRefundHeader> PendingCreditRefunds { get; } = [];
    [ObservableProperty] private CreditRefundHeader? _pendingCreditRefund;
    [ObservableProperty] private bool _creditRefundFundsVerified;
    [ObservableProperty] private bool _creditRefundNotExecutedVerified;
    private string? _creditLoadedAccount;
    private int _creditDocumentsVersion;
    private int _creditSourcePage, _creditTargetPage;
    [ObservableProperty] private bool _hasMoreCreditSources;
    [ObservableProperty] private bool _hasMoreCreditTargets;
    public bool HasPreviousCreditSources => _creditSourcePage > 0;
    public bool HasPreviousCreditTargets => _creditTargetPage > 0;
    public string CreditDocumentPagesLabel => $"A favor: página {_creditSourcePage + 1}. Deuda: página {_creditTargetPage + 1}. Hasta 100 documentos por lista.";
    private void NotifyCreditPages()
    {
        OnPropertyChanged(nameof(HasPreviousCreditSources)); OnPropertyChanged(nameof(HasPreviousCreditTargets));
        OnPropertyChanged(nameof(CreditDocumentPagesLabel));
    }
    [RelayCommand]
    private Task NextCreditSourcesAsync() => RunAsync(async () =>
    { if (HasMoreCreditSources) { ++_creditSourcePage; await LoadCreditDocumentsCoreAsync(false); } });
    [RelayCommand]
    private Task PreviousCreditSourcesAsync() => RunAsync(async () =>
    { if (_creditSourcePage > 0) { --_creditSourcePage; await LoadCreditDocumentsCoreAsync(false); } });
    [RelayCommand]
    private Task NextCreditTargetsAsync() => RunAsync(async () =>
    { if (HasMoreCreditTargets) { ++_creditTargetPage; await LoadCreditDocumentsCoreAsync(false); } });
    [RelayCommand]
    private Task PreviousCreditTargetsAsync() => RunAsync(async () =>
    { if (_creditTargetPage > 0) { --_creditTargetPage; await LoadCreditDocumentsCoreAsync(false); } });
    private AccountKind CreditKind => CreditSupplier ? AccountKind.Supplier : AccountKind.Customer;
    private string CreditAccount => (CreditSupplier ? Supplier?.Id : Customer?.Id)
        ?? throw new InvalidOperationException("Seleccione la cuenta del cliente o proveedor.");

    partial void OnCreditSupplierChanged(bool value) => ResetCreditSelection();
    partial void OnCustomerChanged(CustomerDto? value) { ResetCreditSelection(); ResetLedgerQuery(); }
    partial void OnSupplierChanged(SupplierDto? value) { ResetCreditSelection(); ResetLedgerQuery(); }
    partial void OnCreditSourceChanged(AccountDocumentBalance? value) => CreditActionVerified = false;
    partial void OnCreditTargetChanged(AccountDocumentBalance? value) => CreditActionVerified = false;
    partial void OnCanSettleCreditChanged(bool value) { if (!value) { ResetRefundQuery(true); ResetCreditSelection(); } }
    partial void OnPendingCreditRefundChanged(CreditRefundHeader? value)
    {
        Reference = ""; CreditRefundFundsVerified = false; CreditRefundNotExecutedVerified = false;
        RefundAccountCreditCommand.NotifyCanExecuteChanged(); CancelAccountCreditRefundCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadCreditRefundsCoreAsync()
    {
        ResetRefundQuery(true);
        await LoadRefundPageAsync(true, null, 0);
    }

    [RelayCommand]
    private Task LoadCreditRefundsAsync() => RunAsync(LoadCreditRefundsCoreAsync);

    private void ResetCreditSelection()
    {
        ++_creditDocumentsVersion;
        CreditActionVerified = false; CreditSource = null; CreditTarget = null;
        CreditSources.Clear(); CreditTargets.Clear(); _creditLoadedAccount = null;
        _creditSourcePage = _creditTargetPage = 0; HasMoreCreditSources = HasMoreCreditTargets = false; NotifyCreditPages();
        CreditExplanation = "Consulte los documentos de esta cuenta. Las aplicaciones no mueven efectivo.";
    }
    private async Task<bool> LoadCreditDocumentsCoreAsync(bool reset = true)
    {
        string account = CreditAccount;
        var kind = CreditKind;
        string? source = reset ? null : CreditSource?.DocumentId, target = reset ? null : CreditTarget?.DocumentId;
        if (reset) ResetCreditSelection();
        else
        {
            ++_creditDocumentsVersion; CreditActionVerified = false; CreditSource = null; CreditTarget = null;
            CreditSources.Clear(); CreditTargets.Clear(); _creditLoadedAccount = null;
            HasMoreCreditSources = HasMoreCreditTargets = false; NotifyCreditPages();
        }
        CreditExplanation = "Consulta de documentos pendiente; no confirma un saldo cero.";
        int version = _creditDocumentsVersion;
        AccountDocumentPages documents;
        try { documents = await Credits.GetDocumentPagesAsync(kind, account, _creditSourcePage, _creditTargetPage); }
        catch when (version != _creditDocumentsVersion) { return false; }
        catch { CreditExplanation = "No se pudieron consultar los documentos. No equivale a saldo cero."; throw; }
        if (version != _creditDocumentsVersion) return false;
        foreach (var item in documents.Sources) CreditSources.Add(item);
        foreach (var item in documents.Targets) CreditTargets.Add(item);
        CreditSource = CreditSources.FirstOrDefault(d => d.DocumentId == source);
        CreditTarget = CreditTargets.FirstOrDefault(d => d.DocumentId == target);
        HasMoreCreditSources = documents.HasMoreSources; HasMoreCreditTargets = documents.HasMoreTargets;
        _creditLoadedAccount = account;
        CreditExplanation = $"Saldo a favor disponible de toda la cuenta: {documents.AvailableCredit:C2}. Deuda por documento de toda la cuenta: {documents.Debt:C2}. "
            + (CreditSupplier ? "Reembolsar = RECIBIR dinero del proveedor." : "Reembolsar = ENTREGAR dinero al cliente.");
        return true;
    }
    [RelayCommand]
    private Task LoadCreditDocumentsAsync() => RunAsync(async () => { await LoadCreditDocumentsCoreAsync(); });

    private void ValidateCreditSelection()
    {
        if (_creditLoadedAccount != CreditAccount || CreditSource is null || !CreditSources.Contains(CreditSource))
            throw new InvalidOperationException("Consulte y seleccione un documento a favor de esta cuenta.");
        if (!CreditActionVerified) throw new InvalidOperationException("Revise cuenta, documento, importe y confirme la acción antes de registrarla.");
    }
    [RelayCommand]
    private Task ApplyAccountCreditAsync() => RunAsync(async () =>
    {
        ValidateCreditSelection();
        if (CreditTarget is null || !CreditTargets.Contains(CreditTarget)) throw new InvalidOperationException("Seleccione el documento con deuda que desea liquidar.");
        int version = _creditDocumentsVersion;
        try { await Credits.ApplyAsync(CreditKind, CreditAccount, CreditSource!.DocumentId, CreditTarget.DocumentId, Amount, Reason, Key("CREDIT_APPLY")); }
        catch when (version != _creditDocumentsVersion) { return; }
        _keys.Remove("CREDIT_APPLY");
        if (version != _creditDocumentsVersion) return;
        CreditActionVerified = false;
        Message = "Aplicación registrada. No repita la operación.";
        try { await LoadCreditDocumentsCoreAsync(); }
        catch { Summary = "La aplicación quedó registrada; no se pudo actualizar el saldo. Consulte de nuevo sin repetir la operación."; }
    });

    [RelayCommand]
    private Task PrepareAccountCreditRefundAsync() => RunAsync(async () =>
    {
        ValidateCreditSelection();
        int version = _creditDocumentsVersion;
        string operation = Key("CREDIT_PREPARE");
        Message = $"Solicitud local {operation}. Si se interrumpe, consulte esta referencia antes de mover dinero.";
        try { await Credits.PrepareRefundAsync(CreditKind, CreditAccount, CreditSource!.DocumentId, Amount, Method, Reason, operation); }
        catch when (version != _creditDocumentsVersion) { return; }
        catch (Exception ex) { throw new InvalidOperationException($"No se confirmó la solicitud {operation}. Consulte esa referencia antes de mover dinero. {ex.Message}", ex); }
        _keys.Remove("CREDIT_PREPARE");
        if (version != _creditDocumentsVersion) return;
        CreditActionVerified = false;
        Message = $"Solicitud guardada: {operation}. Saldo reservado; dinero aún no confirmado.";
        try { if (await LoadCreditDocumentsCoreAsync()) await LoadCreditRefundsCoreAsync(); }
        catch { Summary = $"Solicitud {operation} guardada. No se pudo actualizar la pantalla; consulte la misma referencia y no duplique la entrega."; }
    });

    [RelayCommand(CanExecute = nameof(CanResolveSelectedCreditRefund))]
    private Task RefundAccountCreditAsync() => ResolveSelectedCreditRefundAsync(true);

    [RelayCommand(CanExecute = nameof(CanResolveSelectedCreditRefund))]
    private Task CancelAccountCreditRefundAsync() => ResolveSelectedCreditRefundAsync(false);

    private Task ResolveSelectedCreditRefundAsync(bool fundsMoved) => RunAsync(async () =>
    {
        var selected = PendingCreditRefund;
        if (selected is null || !PendingCreditRefunds.Contains(selected) || selected.RequiresReview)
            throw new InvalidOperationException("Seleccione una solicitud vigente sin inconsistencias. No repita la entrega física.");
        int version = _creditRefundNavigation.Version;
        try
        {
            await Credits.ResolveRefundAsync(selected.OperationId, fundsMoved, fundsMoved ? Reference : null, Reason,
            fundsMoved ? CreditRefundFundsVerified : CreditRefundNotExecutedVerified);
        }
        catch when (version != _creditRefundNavigation.Version) { return; }
        if (version != _creditRefundNavigation.Version) return;
        PendingCreditRefunds.Remove(selected); PendingCreditRefund = null;
        if (!await RefreshRefundsAfterCommitAsync(true, $"Resolución registrada: {selected.OperationId}. No vuelva a mover dinero.")) return;
        try { if (Customer is not null || Supplier is not null) await LoadCreditDocumentsCoreAsync(); }
        catch { Summary += " No se pudo actualizar el saldo de la cuenta; vuelva a consultarlo."; }
    });
}
