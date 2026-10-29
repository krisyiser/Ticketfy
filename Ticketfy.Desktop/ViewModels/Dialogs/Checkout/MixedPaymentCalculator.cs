using System;

namespace Ticketfy.ViewModels.Dialogs.Checkout;

/// <summary>
/// Encapsulates strict mixed payment calculation rules (Cash + Card + Wallet math and validation).
/// Extracted from CheckoutDialogViewModel.
/// </summary>
public static class MixedPaymentCalculator
{
    public static (bool isValid, decimal changeAmount, string? errorMessage) Calculate(
        decimal cashAmount,
        decimal cardAmount,
        decimal walletAmount,
        decimal totalBill)
    {
        if (!Ticketfy.Core.Helpers.NumericEntry.IsMoney(cashAmount)
            || !Ticketfy.Core.Helpers.NumericEntry.IsMoney(cardAmount)
            || !Ticketfy.Core.Helpers.NumericEntry.IsMoney(walletAmount)
            || !Ticketfy.Core.Helpers.NumericEntry.IsMoney(totalBill))
            return (false, 0m, "Use importes no negativos, dentro del límite permitido y con hasta dos decimales.");
        decimal digitalPayments = cardAmount + walletAmount;
        if (digitalPayments > totalBill)
        {
            return (false, 0m, "El cobro en Tarjeta y Monedero no puede superar el Total del Ticket.");
        }

        decimal remainingBalanceToPayByCash = totalBill - digitalPayments;
        decimal changeAmount = cashAmount >= remainingBalanceToPayByCash
            ? cashAmount - remainingBalanceToPayByCash
            : 0m;

        return (true, changeAmount, null);
    }
}
