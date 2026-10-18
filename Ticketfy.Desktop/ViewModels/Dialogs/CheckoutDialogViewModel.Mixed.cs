using CommunityToolkit.Mvvm.Input;
using System;

namespace Ticketfy.ViewModels.Dialogs;

public partial class CheckoutDialogViewModel
{
    partial void OnReceivedAmountInputChanged(string value)
    {
        if (decimal.TryParse(value, out var parsed))
        {
            ReceivedAmount = parsed;
            PaidAmount = parsed;
            ChangeAmount = Math.Max(0m, parsed - TotalToPay);
        }
        else
        {
            ReceivedAmount = 0m;
            PaidAmount = 0m;
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
        if (decimal.TryParse(amountArg, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal addValue))
        {
            ReceivedAmount += addValue;
            PaidAmount = ReceivedAmount;
            ReceivedAmountInput = ReceivedAmount.ToString("0.##");
        }
    }

    [RelayCommand]
    private void SetExactCash()
    {
        ReceivedAmount = TotalToPay;
        PaidAmount = ReceivedAmount;
        ReceivedAmountInput = ReceivedAmount.ToString("0.##");
    }

    partial void OnReceivedAmountChanged(decimal value)
    {
        PaidAmount = value;
        ChangeAmount = Math.Max(0m, value - TotalToPay);
        OnPropertyChanged(nameof(IsSufficientAmount));
        OnPropertyChanged(nameof(ChangeOrShortageText));
        OnPropertyChanged(nameof(ChangeTextColor));
        OnPropertyChanged(nameof(ChangeBgColor));
        ConfirmPaymentCommand.NotifyCanExecuteChanged();
    }

    partial void OnCashAmountChanged(decimal value) => ValidateMixedPaymentMath();
    partial void OnCardAmountChanged(decimal value) => ValidateMixedPaymentMath();
    partial void OnWalletAmountChanged(decimal value) => ValidateMixedPaymentMath();

    public decimal TotalReceived => CashAmount + CardAmount + WalletAmount;

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
        // WIPE GHOST STATE
        CashAmount = 0m;
        CardAmount = 0m;
        WalletAmount = 0m;
        ChangeAmount = 0m;
        ErrorMessage = string.Empty;

        if (value != "Efectivo" && value != "Mixto")
        {
            ReceivedAmount = TotalToPay;
            PaidAmount = TotalToPay;
            ReceivedAmountInput = TotalToPay.ToString("0.##");
        }
        else if (value == "Efectivo" && ReceivedAmount == 0m)
        {
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
