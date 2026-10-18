using CommunityToolkit.Mvvm.ComponentModel;

namespace Ticketfy.ViewModels.Dialogs;

/// <summary>
/// Model representing an applied tender line item (Cash, Card, SPEI, Loyalty, etc.) in a checkout transaction.
/// </summary>
public partial class TenderEntryModel : ObservableObject
{
    public string MethodName { get; init; } = "Efectivo";
    public decimal AmountPaid { get; set; }
    public string ReferenceOrFolio { get; set; } = string.Empty;
}
