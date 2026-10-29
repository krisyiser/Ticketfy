using Avalonia.Controls;

namespace Ticketfy.Views.Suppliers;

public partial class PurchaseOrderView : UserControl
{
    public PurchaseOrderView()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, ConfirmPurchaseBtn);
    }
}
