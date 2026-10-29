using Avalonia.Controls;
using Avalonia.Threading;

namespace Ticketfy.Views.Dialogs;

public partial class CheckoutDialog : UserControl
{
    public CheckoutDialog()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, this.FindControl<Button>("ConfirmSaleButton")!);
        this.AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var textBox = this.FindControl<TextBox>("ReceivedAmountTextBox");
            if (textBox != null && textBox.IsVisible)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }, DispatcherPriority.Background);
    }
}
