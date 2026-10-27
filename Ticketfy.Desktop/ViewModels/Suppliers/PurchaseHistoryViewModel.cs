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
    private long _loadVersion;
    private long _detailVersion, _printVersion;
    public void ClearSessionData()
    {
        ++_loadVersion; ++_printVersion; Purchases.Clear(); ClosePurchaseDetail();
        FeedbackMessage = Search = string.Empty; PageNumber = 1; IsLoading = false;
    }

    public ObservableCollection<PurchaseDto> Purchases { get; } = [];
    public ObservableCollection<PurchaseFinancialEntryDto> FinancialEntries { get; } = [];
    private int _financialPage;
    [ObservableProperty] private bool _isLoadingFinancial;
    [ObservableProperty] private bool _hasMoreFinancial;
    [ObservableProperty] private string _financialFeedback = "Consulte los movimientos documentados de esta compra.";
    public bool HasPreviousFinancial => _financialPage > 0;
    private void ClearFinancial()
    {
        FinancialEntries.Clear(); _financialPage = 0; HasMoreFinancial = false; IsLoadingFinancial = false;
        FinancialFeedback = "Consulte los movimientos documentados de esta compra.";
        OnPropertyChanged(nameof(HasPreviousFinancial));
    }
    [RelayCommand] private async Task LoadFinancialAsync() { if (IsLoadingFinancial) return; _financialPage = 0; await ReadFinancialAsync(); }
    [RelayCommand] private async Task NextFinancialAsync() { if (IsLoadingFinancial || !HasMoreFinancial) return; ++_financialPage; await ReadFinancialAsync(); }
    [RelayCommand] private async Task PreviousFinancialAsync() { if (IsLoadingFinancial || _financialPage == 0) return; --_financialPage; await ReadFinancialAsync(); }
    private async Task ReadFinancialAsync()
    {
        var selected = SelectedPurchaseForDetail;
        if (selected is null) return;
        var version = _detailVersion;
        FinancialEntries.Clear(); IsLoadingFinancial = true; HasMoreFinancial = false;
        FinancialFeedback = "Consultando movimientos; no equivale a ausencia de pagos.";
        OnPropertyChanged(nameof(HasPreviousFinancial));
        try
        {
            var page = await _purchaseService.GetFinancialHistoryAsync(selected.Id, _financialPage);
            if (version != _detailVersion) return;
            foreach (var row in page.Items) FinancialEntries.Add(row);
            SelectedPurchaseForDetail = page.Document ?? selected with { Financial = page.Summary, FinancialWarning = page.Warning };
            HasMoreFinancial = page.HasMore;
            FinancialFeedback = $"Página {_financialPage + 1}: {page.Items.Count} de {page.TotalCount} movimientos. " +
                "Importes positivos aumentan la deuda; negativos la reducen. El saldo incluye todas las páginas. " + page.Warning;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Purchase financial history lookup failed");
            if (version != _detailVersion) return;
            SelectedPurchaseForDetail = selected with { Financial = null, FinancialWarning = "Estado financiero no disponible; consulte de nuevo." };
            FinancialFeedback = "No se pudieron consultar los movimientos. No significa saldo ni pagos cero.";
        }
        finally { if (version == _detailVersion) IsLoadingFinancial = false; }
    }

    [ObservableProperty] private PurchaseDto? _selectedPurchaseForDetail;
    [ObservableProperty] private bool _isPurchaseDetailDialogOpen = false;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _pageNumber = 1;
    [ObservableProperty] private bool _isLoadingDetail;
    private const int PageSize = 100;
    partial void OnSearchChanged(string value)
    {
        ++_loadVersion; ++_printVersion;
        Purchases.Clear(); ClosePurchaseDetail(); PageNumber = 1; IsLoading = false;
        FeedbackMessage = "Búsqueda modificada. Pulse Buscar para consultar.";
    }

    public PurchaseHistoryViewModel(IPurchaseService purchaseService, IEscPosPrinterService? printerService = null)
    {
        _purchaseService = purchaseService;
        _printerService = printerService;
    }

    public async Task LoadPurchasesAsync()
    {
        if (IsLoading) return;
        var version = ++_loadVersion;
        ++_printVersion; Purchases.Clear(); ClosePurchaseDetail();
        IsLoading = true;
        try
        {
            var list = await _purchaseService.GetPageAsync(PageNumber - 1, PageSize, Search);
            if (version != _loadVersion) return;
            Purchases.Clear();
            foreach (var p in list) Purchases.Add(p);
            FeedbackMessage = $"Página {PageNumber}: {list.Count} compras. Busque por proveedor o factura para localizar documentos antiguos.";
            if (list.Any(p => !p.DetailsAvailable))
                FeedbackMessage += " Hay desgloses pendientes o no verificables; consulte el estado de cada documento.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseHistoryViewModel: error loading purchases");
            if (version != _loadVersion) return;
            Purchases.Clear(); ClosePurchaseDetail();
            FeedbackMessage = "No se pudo consultar el historial. Revise su sesión y reintente.";
        }
        finally { if (version == _loadVersion) IsLoading = false; }
    }

    [RelayCommand] private async Task SearchPurchasesAsync() { if (IsLoading) return; PageNumber = 1; await LoadPurchasesAsync(); }
    [RelayCommand] private async Task NextPageAsync() { if (IsLoading || Purchases.Count < PageSize) return; PageNumber++; await LoadPurchasesAsync(); }
    [RelayCommand] private async Task PreviousPageAsync() { if (IsLoading || PageNumber <= 1) return; PageNumber--; await LoadPurchasesAsync(); }

    [RelayCommand]
    private async Task ViewPurchaseDetailAsync(PurchaseDto? purchase)
    {
        if (purchase == null) return;
        ClosePurchaseDetail();
        long version = ++_detailVersion, loadVersion = _loadVersion;
        IsLoadingDetail = true;
        try
        {
            var detail = await _purchaseService.GetByIdAsync(purchase.Id)
                ?? throw new InvalidOperationException("El documento ya no está disponible.");
            if (version != _detailVersion || loadVersion != _loadVersion) return;
            SelectedPurchaseForDetail = detail;
            IsPurchaseDetailDialogOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Purchase detail lookup failed");
            if (version == _detailVersion && loadVersion == _loadVersion)
                FeedbackMessage = "No se pudo consultar el detalle. Revise permisos y vuelva a consultar.";
        }
        finally { if (version == _detailVersion) IsLoadingDetail = false; }
    }

    [RelayCommand]
    private void ClosePurchaseDetail()
    {
        IsPurchaseDetailDialogOpen = false;
        SelectedPurchaseForDetail = null;
        ++_detailVersion; IsLoadingDetail = false;
        ClearFinancial();
    }

    [RelayCommand]
    private async Task PrintPurchaseTicketAsync(PurchaseDto? purchase)
    {
        if (purchase == null || _printerService == null)
        {
            FeedbackMessage = "No hay impresora configurada o registro de compra seleccionado.";
            return;
        }

        long version = ++_printVersion, loadVersion = _loadVersion;
        try
        {
            var detail = await _purchaseService.GetByIdAsync(purchase.Id);
            if (version != _printVersion || loadVersion != _loadVersion) return;
            if (detail is null || !detail.DetailsAvailable)
            {
                FeedbackMessage = detail?.DetailWarning ?? "Compra no disponible; no se envió a impresión.";
                return;
            }
            bool sent = await _printerService.PrintPurchaseOrderAsync(detail);
            if (version != _printVersion || loadVersion != _loadVersion) return;
            FeedbackMessage = sent ? $"Ticket de compra {purchase.InvoiceNumber} enviado al controlador; compruebe el papel."
                : "No se confirmó el envío del ticket de compra; revise la impresora.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PurchaseHistoryViewModel: error printing ticket");
            if (version != _printVersion || loadVersion != _loadVersion) return;
            FeedbackMessage = "Error al imprimir el ticket de compra.";
        }
    }
}
