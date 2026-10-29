using Avalonia.Controls;

namespace Ticketfy.Views.Expenses;

public partial class ExpenseEntryView : UserControl
{
    public ExpenseEntryView()
    {
        InitializeComponent();
        Ticketfy.Controls.FormValidation.GuardCommit(this, RegisterExpenseButton);
    }
}
