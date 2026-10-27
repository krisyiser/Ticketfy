using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Suppliers;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

/// <summary>
/// Modular coordinator for the Suppliers screen.
/// Composes Directory, Purchase Order, and Purchase History sub-ViewModels.
/// </summary>
public partial class SuppliersViewModel : ObservableObject
{
    private long _sessionVersion;
    public void ClearSessionData()
    {
        ++_sessionVersion;
        DirectoryVM.ClearSessionData(); OrderVM.ClearSessionData(); HistoryVM.ClearSessionData();
        FeedbackMessage = string.Empty;
    }
    public SupplierDirectoryViewModel DirectoryVM { get; }
    public PurchaseOrderViewModel OrderVM { get; }
    public PurchaseHistoryViewModel HistoryVM { get; }

    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public SuppliersViewModel(ISupplierService supplierService, IPurchaseService purchaseService, IProductService productService, IEscPosPrinterService? printerService = null, bool loadOnCreate = true)
    {
        DirectoryVM = new SupplierDirectoryViewModel(supplierService);
        OrderVM = new PurchaseOrderViewModel(purchaseService, productService, supplierService);
        HistoryVM = new PurchaseHistoryViewModel(purchaseService, printerService);

        OrderVM.PurchaseConfirmed += async _ =>
        {
            await HistoryVM.LoadPurchasesAsync();
            await OrderVM.LoadCatalogAsync(System.Linq.Enumerable.ToList(DirectoryVM.Suppliers));
        };
        DirectoryVM.SuppliersUpdated += async () => await OrderVM.LoadCatalogAsync(System.Linq.Enumerable.ToList(DirectoryVM.Suppliers));
        if (loadOnCreate) _ = LoadAllDataAsync();
    }

    public async Task LoadAllDataAsync()
    {
        var version = _sessionVersion;
        await DirectoryVM.LoadSuppliersAsync();
        if (version != _sessionVersion) return;
        await HistoryVM.LoadPurchasesAsync();
        if (version != _sessionVersion) return;
        await OrderVM.LoadCatalogAsync(System.Linq.Enumerable.ToList(DirectoryVM.Suppliers));
    }

    public Task LoadDataAsync() => LoadAllDataAsync();
}
