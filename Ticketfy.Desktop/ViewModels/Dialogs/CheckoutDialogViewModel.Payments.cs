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

namespace Ticketfy.ViewModels.Dialogs;

public partial class CheckoutDialogViewModel
{
    [RelayCommand]
    private void AddTender(string method)
    {
        decimal amountToAdd = RemainingBalance > 0 ? RemainingBalance : ReceivedAmount;
        if (amountToAdd <= 0) return;

        AppliedTenders.Add(new TenderEntryModel
        {
            MethodName = method,
            AmountPaid = amountToAdd,
            ReferenceOrFolio = DateTime.Now.ToString("HH:mm:ss")
        });

        NotifyTenderChanges();
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
        decimal amountToCover = Math.Min(RemainingBalance, (decimal)CustomerPointsBalance);
        if (amountToCover <= 0) return;

        AppliedTenders.Add(new TenderEntryModel
        {
            MethodName = "Puntos Fidelidad",
            AmountPaid = amountToCover,
            ReferenceOrFolio = $"Canje: {amountToCover:N0} pts"
        });

        CustomerPointsBalance -= (double)amountToCover;
        NotifyTenderChanges();
    }

    private void NotifyTenderChanges()
    {
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
        bool checkoutStarted = false;
        bool paymentAuthorized = false;
        try
        {
            IsProcessing = true;
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

            if (PaymentMethod == "Monedero / Tarjeta de Regalo")
            {
                if (_giftcardService == null || string.IsNullOrWhiteSpace(GiftcardNumber))
                {
                    ErrorMessage = "Ingrese el número de folio de la Tarjeta de Regalo.";
                    return;
                }

                var validation = await _giftcardService.ValidateCardAsync(GiftcardNumber.Trim());
                if (!validation.IsValid || validation.Balance < TotalToPay)
                {
                    ErrorMessage = validation.IsValid
                        ? $"Saldo insuficiente en el monedero. Disponible: ${validation.Balance:N2}."
                        : validation.Error;
                    return;
                }
                finalPaid = TotalToPay;
            }

            var snapshots = _cartItems.Select(i => new SaleItemSnapshotDto(
                ProductId: i.Id,
                Name: i.Name,
                UnitPrice: i.UnitPrice,
                Cost: i.UnitPrice * 0.6m,
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
                cashPortion = TotalToPay;
                cardPortion = 0m;
            }
            else if (PaymentMethod == "Tarjeta Débito/Crédito" || PaymentMethod == "Tarjeta" || PaymentMethod == "Card")
            {
                cashPortion = 0m;
                cardPortion = TotalToPay;
            }

            var saleDto = new SaleDto(
                Id: Guid.NewGuid().ToString(),
                Date: DateTimeOffset.Now.ToString("o"),
                Items: snapshots,
                Total: TotalToPay,
                TotalCost: totalCost,
                Profit: profit,
                PaidAmount: finalPaid,
                ChangeAmount: ChangeAmount,
                PaymentMethod: PaymentMethod,
                CustomerId: SelectedCustomer?.Id,
                IsCredit: isCredit,
                IsCancelled: false,
                CancelledAt: null,
                EstadoFiscal: RequiresInvoice ? "TIMBRADO CFDI 4.0" : "PENDIENTE",
                CashAmount: cashPortion,
                CardAmount: cardPortion,
                OperationId: _checkoutOperationId,
                GiftcardNumber: PaymentMethod == "Monedero / Tarjeta de Regalo" ? GiftcardNumber.Trim() : null,
                GiftcardAmount: PaymentMethod == "Monedero / Tarjeta de Regalo" ? TotalToPay : 0m
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

            await _saleService.BeginCheckoutAsync(saleDto);
            checkoutStarted = true;

            if (PaymentMethod == "Tarjeta Débito/Crédito" || PaymentMethod == "Tarjeta")
            {
                IsWaitingForTerminal = true;
                TerminalStatusMessage = "Por favor, pase la tarjeta por la terminal...";
                _paymentCts = new CancellationTokenSource();

                var terminalResult = await _terminalService.ProcessPaymentAsync(TotalToPay, _checkoutOperationId, _paymentCts.Token);
                IsWaitingForTerminal = false;

                if (!terminalResult.IsSuccess || terminalResult.Data == null)
                {
                    ErrorMessage = terminalResult.ErrorMessage ?? "Cobro rechazado o cancelado en la terminal.";
                    bool requiresReconciliation = await _terminalService.RequiresReconciliationAsync(_checkoutOperationId);
                    await _saleService.MarkCheckoutFailedAsync(_checkoutOperationId, ErrorMessage, requiresReconciliation);
                    if (!requiresReconciliation)
                        _checkoutOperationId = Guid.NewGuid().ToString("N");
                    return;
                }

                paymentAuthorized = true;
                await _saleService.MarkPaymentAuthorizedAsync(_checkoutOperationId, terminalResult.Data.TransactionId);
            }

            var savedSale = await _saleService.SaveAsync(saleDto, fiscalOutbox);
            Log.Information("Sale saved successfully with ID: {SaleId}", savedSale.Id);

            await _printDispatcher.DispatchSaleDocumentsAsync(savedSale, ShouldPrintReceipt);

            if (_onSuccessCallback != null)
            {
                await _onSuccessCallback();
            }

            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing payment in CheckoutDialogViewModel");
            ErrorMessage = ex.Message;
            if (checkoutStarted)
            {
                try
                {
                    await _saleService.MarkCheckoutFailedAsync(_checkoutOperationId, ex.GetBaseException().Message, paymentAuthorized);
                }
                catch (Exception journalError)
                {
                    Log.Error(journalError, "Could not update checkout recovery journal for operation {OperationId}", _checkoutOperationId);
                }
            }
        }
        finally
        {
            IsProcessing = false;
            IsWaitingForTerminal = false;
        }
    }

    [RelayCommand]
    private async Task CancelTerminalPaymentAsync()
    {
        _paymentCts?.Cancel();
        IsWaitingForTerminal = false;
        ErrorMessage = "Cobro cancelado por el cajero.";
        await Task.CompletedTask;
    }

    public async Task<bool> ApplyManualDiscountAsync(double requestedDiscountPercentage, string reason, ISecurityInterceptionService? securityService = null, IAuditService? auditService = null, UserModel? currentUser = null)
    {
        double maxAllowed = currentUser?.Role == SystemRole.CAJERO ? 5.0 : 100.0;
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
                UserId = currentUser?.Id.ToString() ?? "cajero_matriz",
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
