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
    }
    private async Task LoadPendingTransfersAsync()
    {
        PendingTransfers.Clear(); foreach (var item in await Transfers.GetPendingAsync()) PendingTransfers.Add(item);
        PendingTransfer = null;
    }
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
        string destination = TransferToWallet
            ? (await _wallets.GetByCardNumberAsync(WalletNumber) ?? throw new InvalidOperationException("Seleccione un monedero activo.")).Id
            : Customer?.Id ?? throw new InvalidOperationException("Seleccione un cliente.");
        string operation = Key("TRANSFER_PREPARE");
        await Transfers.PrepareAsync(operation, TransferToWallet ? IncomingTransferService.WalletPurpose : IncomingTransferService.CustomerPurpose,
            destination, Amount, Reason);
        Amount = 0m; await LoadPendingTransfersAsync(); PendingTransfer = PendingTransfers.Single(p => p.OperationId == operation);
        Summary = "Intento guardado. No se ha abonado ni recargado saldo. Compruebe el depósito antes de aplicarlo.";
    }, "TRANSFER_PREPARE");
    [RelayCommand]
    private Task ApplyTransferAsync() => RunAsync(async () =>
    {
        await Transfers.ConfirmAndApplyAsync(PendingTransfer?.OperationId ?? throw new InvalidOperationException("Seleccione un intento."), TransferReference, TransferFundsVerified);
        await LoadPendingTransfersAsync(); Summary = "Transferencia aplicada una sola vez; no genera entrada de efectivo.";
    });
    [RelayCommand]
    private Task CancelUnfundedTransferAsync() => RunAsync(async () =>
    {
        await Transfers.CancelUnfundedAsync(PendingTransfer?.OperationId ?? throw new InvalidOperationException("Seleccione un intento."), Reason, TransferNoFundsVerified);
        await LoadPendingTransfersAsync(); Summary = "Intento descartado con declaración de no recepción de fondos; se conserva la evidencia.";
    });
    [RelayCommand]
    private Task RefundPendingTransferAsync() => RunAsync(async () =>
    {
        await Transfers.ResolveByExternalRefundAsync(PendingTransfer?.OperationId ?? throw new InvalidOperationException("Seleccione un intento."),
            TransferRefundReference, Reason, TransferRefundVerified);
        await LoadPendingTransfersAsync(); Summary = "Devolución bancaria documentada. No se generó un abono, recarga ni movimiento de caja.";
    });
    [RelayCommand]
    private Task TransferHistoryAsync() => RunAsync(async () =>
    {
        Rows.Clear();
        foreach (var r in await Transfers.GetReceiptsAsync())
            Rows.Add(new(r.CreatedAtUtc, r.DocumentId, r.Purpose + " · " + r.Outcome + " · Depósito: " + r.ExternalReference
                + (r.RefundReference is null ? "" : " · Reembolso: " + r.RefundReference), r.Amount, 0, r.CashierUserId, r.RegisterId, r.ShiftId));
        Summary = "Últimos 200 depósitos aplicados o devueltos. No son ventas adicionales; deben compararse con el estado de cuenta bancario.";
    });
}
