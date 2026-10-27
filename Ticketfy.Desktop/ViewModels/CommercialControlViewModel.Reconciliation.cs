using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels;

public partial class CommercialControlViewModel
{
    [ObservableProperty] private bool _canHistorical;
    [ObservableProperty] private bool _canTransfers;
    [ObservableProperty] private bool _historicalSupplier;
    [ObservableProperty] private decimal _reconciledBalance;
    [ObservableProperty] private bool _historicalEvidenceVerified;
    [ObservableProperty] private string _historicalPreviewText = "Seleccione un cliente o compra antigua y consulte primero su saldo. No se reconstruirán operaciones ausentes.";
    private HistoricalBalancePreview? _historicalPreview;
    [ObservableProperty] private HistoricalBalanceTargetDto? _historicalCustomer;
    [ObservableProperty] private HistoricalBalanceTargetDto? _historicalPurchase;
    public ObservableCollection<HistoricalBalanceTargetDto> HistoricalCustomers { get; } = [];
    public ObservableCollection<HistoricalBalanceTargetDto> HistoricalPurchases { get; } = [];
    [ObservableProperty] private bool _transferToWallet;
    [ObservableProperty] private PendingTransferDto? _pendingTransfer;
    [ObservableProperty] private string _transferReference = "";
    [ObservableProperty] private string _transferRefundReference = "";
    [ObservableProperty] private bool _transferFundsVerified;
    [ObservableProperty] private bool _transferNoFundsVerified;
    [ObservableProperty] private bool _transferRefundVerified;
    public ObservableCollection<PendingTransferDto> PendingTransfers { get; } = [];
    private IncomingTransferService Transfers => new(_factory, _operations);
    private HistoricalReconciliationService Historical => new(_factory, _operations);
    partial void OnHistoricalCustomerChanged(HistoricalBalanceTargetDto? value) => InvalidateHistoricalPreview();
    partial void OnHistoricalPurchaseChanged(HistoricalBalanceTargetDto? value) => InvalidateHistoricalPreview();
    partial void OnHistoricalSupplierChanged(bool value) => InvalidateHistoricalPreview();
    private void InvalidateHistoricalPreview()
    {
        _historicalPreview = null; HistoricalEvidenceVerified = false;
        HistoricalPreviewText = "Consulte el saldo del documento seleccionado antes de confirmar una conciliación.";
    }
    partial void OnPendingTransferChanged(PendingTransferDto? value)
    {
        TransferReference = value?.Reference ?? "";
        TransferFundsVerified = false; TransferNoFundsVerified = false; TransferRefundVerified = false; TransferRefundReference = "";
        ApplyTransferCommand.NotifyCanExecuteChanged();
        CancelUnfundedTransferCommand.NotifyCanExecuteChanged();
        RefundPendingTransferCommand.NotifyCanExecuteChanged();
    }
    private bool HasTransferSelection() => !TransferHistoryMode && PendingTransfer is not null && PendingTransfers.Contains(PendingTransfer);
    [RelayCommand]
    private Task PreviewHistoricalAsync() => RunAsync(async () =>
    {
        _historicalPreview = null; HistoricalEvidenceVerified = false;
        string id = HistoricalSupplier ? HistoricalPurchase?.Id ?? "" : HistoricalCustomer?.Id ?? "";
        bool supplier = HistoricalSupplier;
        var preview = await Historical.PreviewAsync(supplier ? HistoricalBalanceScope.SupplierPurchase : HistoricalBalanceScope.CustomerAccount, id);
        if (supplier != HistoricalSupplier || id != (supplier ? HistoricalPurchase?.Id : HistoricalCustomer?.Id))
            throw new InvalidOperationException("Cambió la selección; consulte de nuevo su saldo.");
        _historicalPreview = preview;
        ReconciledBalance = _historicalPreview.LedgerBalance;
        HistoricalPreviewText = $"Se conciliará: {_historicalPreview.Label} [{id}]. Ledger: {_historicalPreview.LedgerBalance:C2}; saldo almacenado: {_historicalPreview.CachedBalance?.ToString("C2") ?? "No disponible"}. Indique el SALDO ACTUAL comprobado, no el total original de la factura.";
        await ShowHistoricalHistoryAsync(_historicalPreview);
    });
    private async Task ShowHistoricalHistoryAsync(HistoricalBalancePreview preview)
    {
        Rows.Clear();
        foreach (var r in await Historical.GetHistoryAsync(preview.Scope, preview.TargetId))
            Rows.Add(new(r.CreatedAtUtc, r.TargetId, "AJUSTE: " + r.EvidenceReference, r.Adjustment, r.ConfirmedBalance, r.CashierUserId, r.RegisterId, "Administrativo, sin efectivo"));
    }
    [RelayCommand]
    private Task ConfirmHistoricalAsync() => RunAsync(async () =>
    {
        var preview = _historicalPreview ?? throw new InvalidOperationException("Consulte la vista previa antes de confirmar.");
        await Historical.ConfirmAsync(preview.Scope, preview.TargetId, ReconciledBalance, preview.Fingerprint,
            Reference, Reason, HistoricalEvidenceVerified, Key("HISTORICAL"));
        _historicalPreview = null; HistoricalEvidenceVerified = false;
        HistoricalPreviewText = "Saldo conciliado mediante un asiento nuevo. Documentos originales, efectivo e inventario intactos.";
        await ShowHistoricalHistoryAsync(preview);
    }, "HISTORICAL");
    [RelayCommand]
    private Task PrepareTransferAsync() => RunAsync(async () =>
    {
        int version = _transferQueryVersion;
        bool wallet = TransferToWallet;
        string number = WalletNumber, notes = Reason;
        string? customer = Customer?.Id;
        decimal amount = Amount;
        string operation = Key("TRANSFER_PREPARE");
        try
        {
            string destination = wallet
                ? (await _wallets.GetByCardNumberAsync(number) ?? throw new InvalidOperationException("Seleccione un monedero activo.")).Id
                : customer ?? throw new InvalidOperationException("Seleccione un cliente.");
            if (version != _transferQueryVersion) return;
            await Transfers.PrepareAsync(operation, wallet ? IncomingTransferService.WalletPurpose : IncomingTransferService.CustomerPurpose,
                destination, amount, notes);
        }
        catch when (version != _transferQueryVersion) { return; }
        _keys.Remove("TRANSFER_PREPARE");
        if (version != _transferQueryVersion) return;
        Amount = 0m;
        Message = "Intento guardado. Conserve la referencia: " + operation;
        Summary = "Intento guardado. No se ha abonado ni recargado saldo. Compruebe el depósito antes de aplicarlo.";
        TransferSearch = operation;
        try
        {
            await LoadPendingTransfersAsync();
            PendingTransfer = PendingTransfers.SingleOrDefault(p => p.OperationId == operation);
        }
        catch { Summary += " No se pudo actualizar la lista. Consulte esa misma referencia; no prepare otro intento."; }
    });
    [RelayCommand(CanExecute = nameof(HasTransferSelection))]
    private Task ApplyTransferAsync() => ExecuteSelectedTransferAsync(p =>
        Transfers.ConfirmAndApplyAsync(p.OperationId, TransferReference, TransferFundsVerified),
        "Transferencia aplicada una sola vez; no genera entrada de efectivo.");
    [RelayCommand(CanExecute = nameof(HasTransferSelection))]
    private Task CancelUnfundedTransferAsync() => ExecuteSelectedTransferAsync(p =>
        Transfers.CancelUnfundedAsync(p.OperationId, Reason, TransferNoFundsVerified),
        "Intento descartado con declaración de no recepción de fondos; se conserva la evidencia.");
    [RelayCommand(CanExecute = nameof(HasTransferSelection))]
    private Task RefundPendingTransferAsync() => ExecuteSelectedTransferAsync(p =>
        Transfers.ResolveByExternalRefundAsync(p.OperationId, TransferRefundReference, Reason, TransferRefundVerified),
        "Devolución bancaria documentada. No se generó un abono, recarga ni movimiento de caja.");
    [RelayCommand]
    private Task TransferHistoryAsync() => RunAsync(async () =>
    {
        TransferHistoryMode = true;
        ResetTransferQuery();
        await LoadTransferPageAsync(null, 0);
    });
}
