using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using Serilog;
using System;
using Ticketfy.Core.Configuration;
using Ticketfy.ViewModels.Dialogs.Checkout;

namespace Ticketfy.ViewModels;

public partial class FiscalViewModel : ObservableObject
{
    private readonly ISaleService _saleService;
    private readonly IFiscalIntegrationQueueService? _fiscalQueue;
    private long _sessionVersion;
    public void ClearSessionData()
    {
        ++_sessionVersion; Invoices.Clear(); SelectedSale = SelectedInvoice = null;
        SearchFolio = CustomerRfc = CustomerName = CustomerZipCode = ReplacementUuid = ErrorMessage = SuccessMessage = string.Empty;
        IsLoading = false;
    }

    public bool IsFacturamaAvailable => _fiscalQueue is not null;
    public string FacturamaStatusMessage => LocalDevelopmentMode.FacturamaUnavailableMessage;

    public ObservableCollection<SaleDto> Invoices { get; } = new();

    [ObservableProperty] private string _searchFolio = string.Empty;
    [ObservableProperty] private SaleDto? _selectedSale;
    [ObservableProperty] private SaleDto? _selectedInvoice;
    [ObservableProperty] private string _cancellationMotive = "02";
    [ObservableProperty] private string _replacementUuid = string.Empty;
    public ObservableCollection<string> CancellationMotives { get; } = ["01", "02", "03", "04"];

    [ObservableProperty] private string _customerRfc = string.Empty;
    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerZipCode = string.Empty;

    public ObservableCollection<string> Regimenes { get; } = new() {
        "601 - General de Ley Personas Morales",
        "603 - Personas Morales con Fines no Lucrativos",
        "605 - Sueldos y Salarios e Ingresos Asimilados a Salarios",
        "606 - Arrendamiento",
        "608 - Demás ingresos",
        "612 - Personas Físicas con Actividades Empresariales y Profesionales",
        "616 - Sin obligaciones fiscales",
        "621 - Incorporación Fiscal",
        "626 - Régimen Simplificado de Confianza"
    };
    [ObservableProperty] private string _selectedRegimen = "616 - Sin obligaciones fiscales";

    public ObservableCollection<string> UsosCfdi { get; } = new() {
        "G01 - Adquisición de mercancias",
        "G03 - Gastos en general",
        "I01 - Construcciones",
        "S01 - Sin efectos fiscales",
        "CP01 - Pagos"
    };
    [ObservableProperty] private string _selectedUsoCfdi = "G03 - Gastos en general";

    public ObservableCollection<string> FormasPago { get; } = new() {
        "01 - Efectivo",
        "02 - Cheque nominativo",
        "03 - Transferencia electrónica de fondos",
        "04 - Tarjeta de crédito",
        "28 - Tarjeta de débito",
        "99 - Por definir"
    };
    [ObservableProperty] private string _selectedFormaPago = "01 - Efectivo";

    public ObservableCollection<string> MetodosPago { get; } = new() {
        "PUE - Pago en una sola exhibición",
        "PPD - Pago en parcialidades o diferido"
    };
    [ObservableProperty] private string _selectedMetodoPago = "PUE - Pago en una sola exhibición";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _successMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading = false;

    public FiscalViewModel(ISaleService saleService, IFiscalIntegrationQueueService? fiscalQueue = null, bool loadOnCreate = true)
    {
        _saleService = saleService;
        _fiscalQueue = fiscalQueue;
        if (loadOnCreate) _ = LoadInvoicesAsync();
    }

    [RelayCommand]
    private async Task SearchSaleAsync()
    {
        var version = _sessionVersion;
        if (string.IsNullOrWhiteSpace(SearchFolio)) return;
        IsLoading = true;
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;
        try
        {
            var sale = await _saleService.GetByIdAsync(SearchFolio.Trim());
            if (version != _sessionVersion) return;
            SelectedSale = sale;
            if (SelectedSale == null)
            {
                ErrorMessage = "Venta no encontrada.";
            }
            else
            {
                if (!string.IsNullOrEmpty(SelectedSale.InvoiceId))
                {
                    ErrorMessage = "Esta venta ya ha sido facturada (UUID: " + SelectedSale.InvoiceId + ").";
                    SelectedSale = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error buscando venta");
            ErrorMessage = "Error al buscar la venta.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task EmitInvoiceAsync()
    {
        if (SelectedSale == null) return;
        if (string.IsNullOrWhiteSpace(CustomerRfc) || string.IsNullOrWhiteSpace(CustomerName) || string.IsNullOrWhiteSpace(CustomerZipCode))
        {
            ErrorMessage = "RFC, Nombre y Código Postal son requeridos.";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;

        try
        {
            if (_fiscalQueue is null) throw new InvalidOperationException("La cola fiscal no está disponible.");
            var (request, validationError) = CheckoutInvoiceHandler.BuildInvoiceRequest(
                CustomerRfc.Trim().ToUpper(), CustomerName.Trim(), SelectedUsoCfdi,
                SelectedRegimen, CustomerZipCode.Trim(), SelectedSale.Items);
            if (request is null) { ErrorMessage = validationError ?? "Datos fiscales inválidos."; return; }
            request.PaymentForm = SelectedFormaPago[..2];
            request.PaymentMethod = SelectedMetodoPago[..3];
            await _fiscalQueue.EnqueueIssueAsync(
                SelectedSale.Id, request, $"cfdi-issue-sale-{SelectedSale.Id}");
            await _saleService.UpdateFiscalStatusAsync(SelectedSale.Id, "EN COLA", null, null);
            SuccessMessage = "Factura agregada a la cola fiscal. Se timbrará sin duplicar el folio.";
            SelectedSale = null;
            SearchFolio = string.Empty;
            await LoadInvoicesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error emitiendo factura");
            ErrorMessage = "Error al emitir factura: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task QueueCancellationAsync()
    {
        if (SelectedInvoice is null || string.IsNullOrWhiteSpace(SelectedInvoice.InvoiceId) || _fiscalQueue is null) return;
        try
        {
            await _fiscalQueue.EnqueueCancellationAsync(
                SelectedInvoice.Id, SelectedInvoice.InvoiceId, CancellationMotive,
                string.IsNullOrWhiteSpace(ReplacementUuid) ? null : ReplacementUuid.Trim(),
                $"cfdi-cancel-{SelectedInvoice.InvoiceId}-{CancellationMotive}");
            await _saleService.UpdateFiscalStatusAsync(SelectedInvoice.Id, "CANCELACION EN COLA", SelectedInvoice.UuidSat, SelectedInvoice.SerieFolio);
            SuccessMessage = "Cancelación agregada a la cola fiscal.";
            await LoadInvoicesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error queueing fiscal cancellation");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task QueueReconciliationAsync()
    {
        if (SelectedInvoice is null || string.IsNullOrWhiteSpace(SelectedInvoice.InvoiceId) || _fiscalQueue is null) return;
        try
        {
            await _fiscalQueue.EnqueueReconciliationAsync(
                SelectedInvoice.Id, SelectedInvoice.InvoiceId,
                $"cfdi-reconcile-{SelectedInvoice.InvoiceId}-{DateTimeOffset.UtcNow:yyyyMMddHH}");
            SuccessMessage = "Consulta, XML y PDF agregados a la cola de conciliación.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error queueing fiscal reconciliation");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LoadInvoicesAsync()
    {
        if (IsLoading) return;
        var version = _sessionVersion;
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var sales = await _saleService.GetHistoryAsync(100);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version != _sessionVersion) return;
                Invoices.Clear();
                foreach (var sale in sales.Where(s => !string.IsNullOrEmpty(s.InvoiceId) || s.EstadoFiscal is "PENDIENTE" or "EN COLA" or "ERROR AL TIMBRAR" or "TIMBRADO" or "CANCELACION EN COLA"))
                {
                    Invoices.Add(sale);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading invoices in FiscalViewModel");
            if (version != _sessionVersion) return;
            Invoices.Clear(); SelectedInvoice = null;
            ErrorMessage = "No se pudieron cargar los registros fiscales.";
        }
        finally
        {
            if (version == _sessionVersion) IsLoading = false;
        }
    }
}
