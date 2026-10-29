using Avalonia.Controls;

namespace Ticketfy.Views.Dialogs;

public partial class CustomerDialog : UserControl
{
    public CustomerDialog()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, this.FindControl<Button>("SaveCustomerButton")!);
    }
}
