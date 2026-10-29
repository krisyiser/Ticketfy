using Avalonia.Controls;

namespace Ticketfy.Views.Dialogs;

public partial class PromotionDialog : UserControl
{
    public PromotionDialog()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, this.FindControl<Button>("SavePromotionButton")!);
    }
}
