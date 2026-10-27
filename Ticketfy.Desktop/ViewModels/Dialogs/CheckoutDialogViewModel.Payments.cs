using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;
using Ticketfy.Core.Models;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Enums;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels.Dialogs;

public partial class CheckoutDialogViewModel
{
    [RelayCommand]
    private void AddTender(string method)
    {
        ErrorMessage = "Use los campos de pago mixto; los pagos bancarios requieren confirmación real.";
    }

    [RelayCommand]
    private void RemoveTender(TenderEntryModel? tender)
    {
        if (tender == null) return;
        AppliedTenders.Remove(tender);
        NotifyTenderChanges();
    }

    [RelayCommand]
    private void RedeemLoyaltyPoints()
    {
        if (SelectedCustomer == null || CustomerPointsBalance <= 0) return;
        if (AppliedTenders.Count > 0) return;
        PaymentMethod = "Efectivo";
        decimal amountToCover = Math.Round(Math.Min(TotalToPay, (decimal)CustomerPointsBalance), 2);
        if (amountToCover <= 0) return;

        AppliedTenders.Add(new TenderEntryModel
        {
            MethodName = "Puntos Fidelidad",
            AmountPaid = amountToCover,
            ReferenceOrFolio = $"Canje: {amountToCover:N0} pts"
        });

        NotifyTenderChanges();
    }

    private void NotifyTenderChanges()
    {
        PaidAmount = ReceivedAmount + TotalApplied;
        ChangeAmount = Math.Max(0m, PaidAmount - TotalToPay);
        OnPropertyChanged(nameof(TotalApplied));
        OnPropertyChanged(nameof(RemainingBalance));
        OnPropertyChanged(nameof(IsFullyPaid));
        OnPropertyChanged(nameof(IsSufficientAmount));
        OnPropertyChanged(nameof(ChangeOrShortageText));
        OnPropertyChanged(nameof(ChangeTextColor));
        OnPropertyChanged(nameof(ChangeBgColor));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsProcessing)
        {
            ErrorMessage = "Hay una operación en curso. Detener la espera no confirma una cancelación bancaria.";
            return;
        }
        OnDismissed();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ValidateGiftcardAsync()
    {
        if (_giftcardService == null) return;
        if (string.IsNullOrWhiteSpace(GiftcardNumber))
        {
            ErrorMessage = "Ingrese el número de tarjeta / monedero.";
            return;
        }

        var (isValid, balance, error) = await _giftcardService.ValidateCardAsync(GiftcardNumber.Trim());
        if (!isValid)
        {
            ErrorMessage = error;
            return;
        }

        decimal available = balance;
        decimal applied = Math.Min(TotalToPay, available);
        ReceivedAmount = applied;
        ErrorMessage = $"Monedero validado. Saldo disponible: ${available:F2} (Aplicado: ${applied:F2})";
    }

    [RelayCommand(CanExecute = nameof(CanConfirmPayment))]
    private async Task ConfirmPaymentAsync()
    {
        if (!CanConfirmPayment()) return;
        bool checkoutAttempted = false;
        bool paymentAuthorized = false;
        bool terminalAttempted = false;
        try
        {
            IsProcessing = true;
            Ticketfy.Core.Operations.CommercialDocumentLimits.RequireLineCount(_cartItems.Count);
            bool isCredit = IsCreditPayment;
            if (isCredit && SelectedCustomer == null)
            {
                ErrorMessage = "Debe asignar un cliente registrado para cobrar a crédito.";
                return;
            }

            if (isCredit && AvailableCredit < TotalToPay)
            {
                ErrorMessage = $"Crédito insuficiente. Disponible: ${AvailableCredit:N2} — Requerido: ${TotalToPay:N2}";
                return;
            }

            decimal finalPaid = PaymentMethod == "Mixto" ? CashAmount + CardAmount + WalletAmount : PaidAmount;

            if (PaymentMethod == "Mixto")
            {
                if (finalPaid < TotalBill)
                {
                    ErrorMessage = "El monto pagado es insuficiente.";
                    return;
                }
            }
            else if (PaidAmount < TotalToPay && PaymentMethod != "Monedero / Tarjeta de Regalo" && !isCredit)
            {
                ErrorMessage = "El monto pagado es insuficiente.";
                return;
            }

            if (PaymentMethod == "Monedero / Tarjeta de Regalo" || (PaymentMethod == "Mixto" && WalletAmount > 0))
            {
                if (_giftcardService == null || string.IsNullOrWhiteSpace(GiftcardNumber))
                {
                    ErrorMessage = "Ingrese el número de folio de la Tarjeta de Regalo.";
                    return;
                }

                var validation = await _giftcardService.ValidateCardAsync(GiftcardNumber.Trim());
                if (!validation.IsValid || validation.Balance < (PaymentMethod == "Mixto" ? WalletAmount : TotalToPay))
                {
                    ErrorMessage = validation.IsValid
                        ? $"Saldo insuficiente en el monedero. Disponible: ${validation.Balance:N2}."
                        : validation.Error;
                    return;
                }
                if (PaymentMethod != "Mixto") finalPaid = TotalToPay;
            }

            var snapshots = _cartItems.Select(i => new SaleItemSnapshotDto(
                ProductId: i.Id,
                Name: i.Name,
                UnitPrice: i.UnitPrice,
                Cost: i.Cost,
                Quantity: i.Quantity,
                Unit: i.Unit,
                Category: i.Category ?? "General",
                Discount: i.AppliedDiscountAmount,
                TotalPrice: i.TotalPrice,
                OriginalUnitPrice: i.OriginalUnitPrice > 0 ? i.OriginalUnitPrice : i.UnitPrice,
                AppliedDiscountAmount: i.AppliedDiscountAmount,
                AppliedPromotionId: i.AppliedPromotionId,
                SatProductCode: i.SatProductCode,
                SatUnitCode: i.SatUnitCode,
                PointsRewarded: i.PointsRewarded
            )).ToList();

            var totalCost = snapshots.Sum(s => s.Cost * (decimal)s.Quantity);
            var profit = TotalToPay - totalCost;

            decimal cashPortion = 0m;
            decimal cardPortion = 0m;

            if (PaymentMethod == "Mixto")
            {
                // CashAmount is persisted as the portion retained by the drawer,
                // not the tendered amount. Any change is paid from this portion.
                cashPortion = Math.Max(0m, CashAmount - ChangeAmount);
                cardPortion = CardAmount;
            }
            else if (PaymentMethod == "Efectivo" || PaymentMethod == "Cash")
            {
                cashPortion = TotalToPay - TotalApplied;
                cardPortion = 0m;
            }
            else if (PaymentMethod == "Tarjeta Débito/Crédito" || PaymentMethod == "Tarjeta" || PaymentMethod == "Card")
            {
                cashPortion = 0m;
                cardPortion = TotalToPay;
            }

            var saleDto = new SaleDto(
                Id: _checkoutOperationId,
                Date: DateTimeOffset.Now.ToString("o"),
                Items: snapshots,
                Total: TotalToPay,
                TotalCost: totalCost,
                Profit: profit,
                PaidAmount: finalPaid,
                ChangeAmount: ChangeAmount,
                PaymentMethod: PaymentMethod,
                CustomerId: string.IsNullOrWhiteSpace(SelectedCustomer?.Id) ? null : SelectedCustomer.Id,
                IsCredit: isCredit,
                IsCancelled: false,
                CancelledAt: null,
                EstadoFiscal: RequiresInvoice ? "TIMBRADO CFDI 4.0" : "PENDIENTE",
                CashAmount: cashPortion,
                CardAmount: cardPortion,
                OperationId: _checkoutOperationId,
                GiftcardNumber: PaymentMethod == "Monedero / Tarjeta de Regalo" || WalletAmount > 0 ? GiftcardNumber.Trim() : null,
                GiftcardAmount: PaymentMethod == "Monedero / Tarjeta de Regalo" ? TotalToPay : PaymentMethod == "Mixto" ? WalletAmount : 0m,
                GlobalDiscountAmount: snapshots.Sum(i => i.TotalPrice) - TotalToPay,
                Payments: TotalApplied > 0 ? (cashPortion > 0
                    ? new System.Collections.Generic.List<SaleTenderDto> { new(TenderKind.Cash, cashPortion), new(TenderKind.Loyalty, TotalApplied, AccountId: SelectedCustomer?.Id) }
                    : new System.Collections.Generic.List<SaleTenderDto> { new(TenderKind.Loyalty, TotalApplied, AccountId: SelectedCustomer?.Id) }) : null
            );

            FiscalOutboxRequest? fiscalOutbox = null;
            if (RequiresInvoice)
            {
                var (request, validationError) = Checkout.CheckoutInvoiceHandler.BuildInvoiceRequest(
                    FiscalRfc, FiscalRazonSocial, FiscalUsoCfdi, FiscalRegime, FiscalZipCode, snapshots);
                if (request is null)
                {
                    ErrorMessage = validationError ?? "Datos fiscales inválidos.";
                    IsProcessing = false;
                    return;
                }
                fiscalOutbox = new FiscalOutboxRequest(request, $"cfdi-issue-{_checkoutOperationId}");
                saleDto = saleDto with { EstadoFiscal = "EN COLA" };
            }

            ThrowIfDismissed();
            checkoutAttempted = true;
            await _saleService.BeginCheckoutAsync(saleDto, fiscalOutbox);
            ThrowIfDismissed();

            if (IsBankTransfer)
            {
                if (!BankPaymentVerified || _reconciliation is null) throw new InvalidOperationException("Verifique el depósito en la cuenta bancaria y confirme la referencia.");
                await _reconciliation.ConfirmTransferAsync(_checkoutOperationId, TotalToPay, BankReference);
                paymentAuthorized = true;
                ThrowIfDismissed();
            }

            if (cardPortion > 0)
            {
                IsWaitingForTerminal = true;
                TerminalStatusMessage = "Por favor, pase la tarjeta por la terminal...";
                _paymentCts = new CancellationTokenSource();
                terminalAttempted = true;
                var terminalResult = await _terminalService.ProcessPaymentAsync(cardPortion, _checkoutOperationId, _paymentCts.Token);
                IsWaitingForTerminal = false;
                ThrowIfDismissed();

                if (!terminalResult.IsSuccess || terminalResult.Data == null)
                {
                    ErrorMessage = terminalResult.ErrorMessage ?? "No se pudo confirmar el resultado del cobro.";
                    bool requiresReconciliation = await _terminalService.RequiresReconciliationAsync(_checkoutOperationId);
                    await RecordFailureAsync(ErrorMessage, requiresReconciliation);
                    return;
                }

                paymentAuthorized = true;
                await _saleService.MarkPaymentAuthorizedAsync(_checkoutOperationId, terminalResult.Data.TransactionId);
                ThrowIfDismissed();
            }

            var savedSale = await _saleService.SaveAsync(saleDto, fiscalOutbox);
            _committed = true;
            Log.Information("Sale saved successfully with ID: {SaleId}", savedSale.Id);
            if (_dismissed) return;

            try { await _printDispatcher.DispatchSaleDocumentsAsync(savedSale, ShouldPrintReceipt); }
            catch (Exception printError)
            {
                Log.Error(printError, "Sale {SaleId} is committed; receipt can be reprinted from history", savedSale.Id);
                Ticketfy.Core.Messages.HardwareFeedbackMessage.Report($"Venta {savedSale.Id} registrada. Falló el envío a impresión; revise el historial antes de reimprimir. No vuelva a cobrar.", true);
            }

            await NotifyCommittedAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing payment in CheckoutDialogViewModel");
            if (_committed)
            {
                ErrorMessage = "Venta registrada. Consulte el historial; no vuelva a cobrar este ticket.";
                if (!_dismissed) RequestClose?.Invoke();
                return;
            }
            ErrorMessage = ex.Message;
            if (checkoutAttempted)
            {
                try
                {
                    await RecordFailureAsync(ex.GetBaseException().Message, paymentAuthorized || terminalAttempted);
                }
                catch (Exception journalError)
                {
                    Log.Error(journalError, "Could not update checkout recovery journal for operation {OperationId}", _checkoutOperationId);
                    if (_committed)
                    {
                        RequiresReconciliation = false;
                        ErrorMessage = "Venta registrada. Falló la actualización de la pantalla; consulte el historial y no vuelva a cobrar.";
                        if (!_dismissed) RequestClose?.Invoke();
                    }
                    else
                    {
                        RequiresReconciliation = true;
                        ErrorMessage = $"{CheckoutStatePolicy.ReconciliationRequired} Referencia: {_checkoutOperationId}. No se pudo confirmar el estado local.";
                    }
                }
            }
        }
        finally
        {
            IsProcessing = false;
            IsWaitingForTerminal = false;
            _paymentCts?.Dispose();
            _paymentCts = null;
        }
    }

    [RelayCommand]
    private async Task CancelTerminalPaymentAsync()
    {
        _paymentCts?.Cancel();
        TerminalStatusMessage = "Deteniendo la espera; el banco aún puede completar la operación.";
        ErrorMessage = "Detener la espera no confirma una cancelación bancaria. No vuelva a cobrar; conserve la referencia.";
        await Task.CompletedTask;
    }

    private void ThrowIfDismissed()
    {
        if (_dismissed) throw new OperationCanceledException("Se cerró la sesión o el diálogo; concilie la intención original.");
    }

    private async Task RecordFailureAsync(string reason, bool uncertain)
    {
        RequiresReconciliation = true;
        var state = await _saleService.MarkCheckoutFailedAsync(_checkoutOperationId, reason, uncertain);
        if (state == CheckoutOperationState.Completed)
        {
            _committed = true; RequiresReconciliation = false;
            ErrorMessage = "Venta registrada. Consulte el historial; no vuelva a cobrar este ticket.";
            await NotifyCommittedAsync();
        }
        else if (state is null or CheckoutOperationState.Failed)
        {
            RequiresReconciliation = false;
            _checkoutOperationId = Guid.NewGuid().ToString("N");
            OnPropertyChanged(nameof(OperationReference));
        }
        else ErrorMessage = $"{CheckoutStatePolicy.ReconciliationRequired} Referencia: {_checkoutOperationId}.";
    }

    private async Task NotifyCommittedAsync()
    {
        if (_dismissed) return;
        if (!_successNotified && _onSuccessCallback is not null)
        {
            _successNotified = true;
            await _onSuccessCallback();
        }
        if (!_dismissed) RequestClose?.Invoke();
    }

    public async Task<bool> ApplyManualDiscountAsync(double requestedDiscountPercentage, string reason, ISecurityInterceptionService? securityService = null, IAuditService? auditService = null, UserModel? currentUser = null)
    {
        if (currentUser is null || !double.IsFinite(requestedDiscountPercentage) || requestedDiscountPercentage < 0 || requestedDiscountPercentage > 100 || string.IsNullOrWhiteSpace(reason)) return false;
        double maxAllowed = currentUser.Role == SystemRole.CAJERO ? 5.0 : 100.0;
        string? supervisorId = null;

        if (requestedDiscountPercentage > maxAllowed)
        {
            if (securityService != null)
            {
                var authResult = await securityService.AuthorizeHighRiskActionAsync(
                    "Autorización de Descuento Especial",
                    $"El descuento del {requestedDiscountPercentage:N2}% supera el límite permitido para cajeros ({maxAllowed:N2}%).");

                if (!authResult.IsAuthorized) return false;
                supervisorId = authResult.SupervisorId;
            }
            else
            {
                return false;
            }
        }

        decimal subtotalBase = TotalToPay;
        decimal discountAmount = Math.Round(subtotalBase * ((decimal)requestedDiscountPercentage / 100m), 2);
        TotalToPay = Math.Max(0m, subtotalBase - discountAmount);

        if (auditService != null)
        {
            await auditService.LogAsync(new AuditLogEntity
            {
                UserId = currentUser.Id.ToString(),
                AuthorizedBySupervisorId = supervisorId,
                ActionType = Ticketfy.Core.Enums.AuditActionType.ManualDiscountExceeded,
                RiskLevel = Ticketfy.Core.Enums.RiskLevel.HighRisk,
                EntityName = "CheckoutTicket",
                OldValue = "0%",
                NewValue = $"{requestedDiscountPercentage:N2}%",
                FinancialImpact = discountAmount,
                Reason = reason
            });
        }

        return true;
    }
}
