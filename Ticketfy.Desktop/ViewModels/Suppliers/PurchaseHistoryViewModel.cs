using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Suppliers;

/// <summary>
/// Manages purchase order history logs, detail modal state, and ESC/POS ticket printing.
/// Extracted from SuppliersViewModel.
/// </summary>
public partial class PurchaseHistoryViewModel : ObservableObject
{
    private readonly IPurchaseService _purchaseService;
    private readonly IEscPosPrinterService? _printerService;

    public ObservableCollection<PurchaseDto> Purchases { get; } = [];

    [ObservableProperty] private PurchaseDto? _selectedPurchaseForDetail;
    [ObservableProperty] private bool _isPurchaseDetailDialogOpen = false;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public PurchaseHistoryViewModel(IPurchaseService purchaseService, IEscPosPrinterService? printerService = null)
    {
        _purchaseService = purchaseService;
        _printerService = printerService;
    }

    public async Task LoadPurchasesAsync()
    {
        try
        {
            var list = await _purchaseService.GetAllAsync();
            Purchases.Clear();
            foreach (var p in list.OrderByDescending(x => x.Date)) Purchases.Add(p);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseHistoryViewModel: error loading purchases");
        }
    }

    [RelayCommand]
    private void ViewPurchaseDetail(PurchaseDto? purchase)
    {
        if (purchase == null) return;
        SelectedPurchaseForDetail = purchase;
        IsPurchaseDetailDialogOpen = true;
    }

    [RelayCommand]
    private void ClosePurchaseDetail()
    {
        IsPurchaseDetailDialogOpen = false;
        SelectedPurchaseForDetail = null;
    }

    [RelayCommand]
    private async Task PrintPurchaseTicketAsync(PurchaseDto? purchase)
    {
        if (purchase == null || _printerService == null)
        {
            FeedbackMessage = "No hay impresora configurada o registro de compra seleccionado.";
            return;
        }

        try
        {
            bool sent = await _printerService.PrintPurchaseOrderAsync(purchase);
            FeedbackMessage = sent ? $"Ticket de compra {purchase.InvoiceNumber} enviado al controlador; compruebe el papel."
                : "No se confirmó el envío del ticket de compra; revise la impresora.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseHistoryViewModel: error printing ticket");
            FeedbackMessage = "Error al imprimir el ticket de compra.";
        }
    }
}
