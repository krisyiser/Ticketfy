using Avalonia.Controls;

namespace Ticketfy.Views.Dialogs;

public partial class ProductDialog : UserControl
{
    public ProductDialog()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, SaveProductButton);
    }
}
