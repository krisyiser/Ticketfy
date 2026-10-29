using CommunityToolkit.Mvvm.Input;
using System;

namespace Ticketfy.ViewModels.Dialogs;

public partial class CheckoutDialogViewModel
{
    partial void OnReceivedAmountInputChanged(string value)
    {
        if (Ticketfy.Core.Helpers.NumericEntry.TryDecimal(value, out var parsed) && Ticketfy.Core.Helpers.NumericEntry.IsMoney(parsed))
        {
            ReceivedAmount = parsed;
            PaidAmount = parsed + TotalApplied;
            ChangeAmount = Math.Max(0m, PaidAmount - TotalToPay);
        }
        else
        {
            ReceivedAmount = 0m;
            PaidAmount = TotalApplied;
            ChangeAmount = 0m;
        }

        OnPropertyChanged(nameof(IsSufficientAmount));
        OnPropertyChanged(nameof(ChangeOrShortageText));
        OnPropertyChanged(nameof(ChangeTextColor));
        OnPropertyChanged(nameof(ChangeBgColor));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddCashDenomination(string amountArg)
    {
        if (!CanEditTender) return;
        if (decimal.TryParse(amountArg, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal addValue))
        {
            ReceivedAmount += addValue;
            PaidAmount = ReceivedAmount + TotalApplied;
            ReceivedAmountInput = ReceivedAmount.ToString("0.##");
        }
    }

    [RelayCommand]
    private void SetExactCash()
    {
        if (!CanEditTender) return;
        ReceivedAmount = TotalToPay - TotalApplied;
        PaidAmount = ReceivedAmount + TotalApplied;
        ReceivedAmountInput = ReceivedAmount.ToString("0.##");
    }

    partial void OnReceivedAmountChanged(decimal value)
    {
        PaidAmount = value + TotalApplied;
        ChangeAmount = Math.Max(0m, PaidAmount - TotalToPay);
        OnPropertyChanged(nameof(IsSufficientAmount));
        OnPropertyChanged(nameof(ChangeOrShortageText));
        OnPropertyChanged(nameof(ChangeTextColor));
        OnPropertyChanged(nameof(ChangeBgColor));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    partial void OnCashAmountChanged(decimal value) => ValidateMixedPaymentMath();
    partial void OnCardAmountChanged(decimal value) => ValidateMixedPaymentMath();
    partial void OnWalletAmountChanged(decimal value) => ValidateMixedPaymentMath();

    public decimal TotalReceived => TotalMixedReceived;

    partial void OnTotalToPayChanged(decimal value)
    {
        TotalBill = value;
        if (IsMixedPayment) ValidateMixedPaymentMath();
        else OnReceivedAmountChanged(ReceivedAmount);
    }

    private void ValidateMixedPaymentMath()
    {
        OnPropertyChanged(nameof(TotalReceived));
        OnPropertyChanged(nameof(TotalMixedReceived));
        OnPropertyChanged(nameof(IsSufficientAmount));
        OnPropertyChanged(nameof(ChangeOrShortageText));

        var (isValid, chgAmount, error) = Checkout.MixedPaymentCalculator.Calculate(CashAmount, CardAmount, WalletAmount, TotalBill);
        if (!isValid && error != null)
        {
            ErrorMessage = error;
            ChangeAmount = 0m;
        }
        else
        {
            ErrorMessage = string.Empty;
            ChangeAmount = chgAmount;
        }

        OnPropertyChanged(nameof(ChangeTextColor));
        OnPropertyChanged(nameof(ChangeBgColor));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    partial void OnPaymentMethodChanged(string value)
    {
        AppliedTenders.Clear();
        NotifyTenderChanges();
        // WIPE GHOST STATE
        CashAmount = 0m;
        CardAmount = 0m;
        WalletAmount = 0m;
        ChangeAmount = 0m;
        ErrorMessage = string.Empty;

        BankReference = string.Empty;
        BankPaymentVerified = false;
        OnPropertyChanged(nameof(IsBankTransfer));

        if (value != "Efectivo" && value != "Mixto")
        {
            ReceivedAmount = TotalToPay;
            PaidAmount = TotalToPay;
            ReceivedAmountInput = TotalToPay.ToString("0.##");
        }
        else
        {
            ReceivedAmount = 0m;
            PaidAmount = 0m;
            ReceivedAmountInput = "0";
        }

        // TRIGGER UI UPDATES
        OnPropertyChanged(nameof(IsCashPayment));
        OnPropertyChanged(nameof(IsMixedPayment));
        OnPropertyChanged(nameof(IsNotMixedPayment));
        OnPropertyChanged(nameof(IsCreditPayment));
        OnPropertyChanged(nameof(CreditIsInsufficient));
        OnPropertyChanged(nameof(CreditAvailableDisplay));
        OnPropertyChanged(nameof(TotalReceived));
        OnPropertyChanged(nameof(IsSufficientAmount));
        OnPropertyChanged(nameof(ChangeOrShortageText));
        OnPropertyChanged(nameof(ChangeTextColor));
        OnPropertyChanged(nameof(ChangeBgColor));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }
}
