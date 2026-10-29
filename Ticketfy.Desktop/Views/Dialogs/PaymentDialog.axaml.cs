using Avalonia.Controls;

namespace Ticketfy.Views.Dialogs;

public partial class PaymentDialog : UserControl
{
    public PaymentDialog()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, this.FindControl<Button>("SaveCustomerPaymentButton")!);
    }
}
