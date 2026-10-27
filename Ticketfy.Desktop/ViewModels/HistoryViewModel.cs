using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Helpers;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public record HourlySalesDto(string HourLabel, int TicketCount, decimal TotalRevenue, bool IsPeakHour);

public partial class HistoryViewModel : ObservableObject
{
    private readonly ISaleService _saleService;
    private readonly IEscPosPrinterService _printerService;
    private readonly IShiftService? _shiftService;
    private readonly ISettingsService? _settingsService;
    private long _salesVersion, _performanceVersion, _settingsVersion, _cashupsVersion, _detailVersion;
    private int _salesPage, _cashupPage;
    [ObservableProperty] private bool _hasMoreSales;
    [ObservableProperty] private int _matchingSalesCount;
    public bool HasPreviousSales => _salesPage > 0;
    public string SalesPageLabel => $"Página {_salesPage + 1} · {Sales.Count} de {MatchingSalesCount} tickets";
    [ObservableProperty] private bool _hasMoreCashups;
    [ObservableProperty] private bool _isLoadingCashups;
    [ObservableProperty] private int _matchingCashupCount;
    [ObservableProperty] private string _cashupFeedbackMessage = "";
    public bool HasPreviousCashups => _cashupPage > 0;
    public string CashupPageLabel => $"Página {_cashupPage + 1} · {Cashups.Count} de {MatchingCashupCount} cortes";
    private bool _settingCommission;
    private bool _acceptEventRefresh;
    public void Deactivate() => _acceptEventRefresh = false;
    public void ClearSessionData()
    {
        _acceptEventRefresh = false;
        ++_salesVersion; ++_performanceVersion; ++_settingsVersion; ++_cashupsVersion;
        Sales.Clear(); Cashups.Clear(); HourlyReport.Clear(); CashierPerformanceReport.Clear();
        CloseSaleDetail(); CloseCashupDetail();
        PeakHourRevenue = TotalDayRevenue = 0m; PeakHourLabel = "Ninguna";
        _salesPage = 0; HasMoreSales = false; MatchingSalesCount = 0;
        NotifySalesPage();
        ResetCashupPage();
        FeedbackMessage = string.Empty; IsLoading = false;
        SetLoadedCommission(0m);
    }

    private void SetLoadedCommission(decimal value)
    {
        _settingCommission = true;
        try { CommissionPercentage = value; }
        finally { _settingCommission = false; }
    }

    private const string CommissionKey = "HistoryCommissionPercentage";

    public ObservableCollection<SaleDto> Sales { get; } = [];
    public ObservableCollection<HourlySalesDto> HourlyReport { get; } = [];
    public ObservableCollection<CashierPerformanceDto> CashierPerformanceReport { get; } = [];
    public ObservableCollection<Ticketfy.Data.Entities.CashupEntity> Cashups { get; } = [];

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private string _peakHourLabel = "Ninguna";
    [ObservableProperty] private decimal _peakHourRevenue;
    [ObservableProperty] private decimal _totalDayRevenue;
    [ObservableProperty] private DateTime? _startDate = Ticketfy.Core.Helpers.BusinessTime.Today;
    [ObservableProperty] private DateTime? _endDate = Ticketfy.Core.Helpers.BusinessTime.Today;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private decimal _commissionPercentage = 0m;

    partial void OnStartDateChanged(DateTime? value) => InvalidatePeriod();
    partial void OnEndDateChanged(DateTime? value) => InvalidatePeriod();
    private void InvalidatePeriod()
    {
        ++_salesVersion; ++_performanceVersion; ++_cashupsVersion;
        _salesPage = 0; HasMoreSales = false; MatchingSalesCount = 0; IsLoading = false;
        Sales.Clear(); Cashups.Clear(); CashierPerformanceReport.Clear();
        CloseSaleDetail(); CloseCashupDetail(); ApplyHourlySummary([]);
        FeedbackMessage = "Fechas modificadas; pulse Filtrar para consultar el nuevo período.";
        NotifySalesPage();
        ResetCashupPage();
    }
    private void NotifySalesPage()
    {
        OnPropertyChanged(nameof(SalesPageLabel)); OnPropertyChanged(nameof(HasPreviousSales));
    }
    private void ResetCashupPage()
    {
        _cashupPage = 0; HasMoreCashups = false; MatchingCashupCount = 0; IsLoadingCashups = false;
        CashupFeedbackMessage = ""; NotifyCashupPage();
    }
    private void NotifyCashupPage()
    {
        OnPropertyChanged(nameof(CashupPageLabel)); OnPropertyChanged(nameof(HasPreviousCashups));
    }
    [RelayCommand]
    private async Task NextCashupPageAsync()
    { if (HasMoreCashups && !IsLoadingCashups) { ++_cashupPage; await LoadCashupsAsync(); } }
    [RelayCommand]
    private async Task PreviousCashupPageAsync()
    { if (_cashupPage > 0 && !IsLoadingCashups) { --_cashupPage; await LoadCashupsAsync(); } }
    [RelayCommand]
    private async Task NextSalesPageAsync()
    { if (HasMoreSales && !IsLoading) { ++_salesPage; await LoadSalesPageAsync(); } }
    [RelayCommand]
    private async Task PreviousSalesPageAsync()
    { if (_salesPage > 0 && !IsLoading) { --_salesPage; await LoadSalesPageAsync(); } }

    // Custom Tab State
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabArqueos))]
    private bool _isTabVentas = true;
    public bool IsTabArqueos => !IsTabVentas;

    [RelayCommand]
    private void SelectHistoryTab(string tab)
    {
        IsTabVentas = tab == "ventas";
    }

    partial void OnCommissionPercentageChanged(decimal value)
    {
        if (_settingCommission) return;
        if (value < 0m || value > 100m || Math.Round(value, 1) != value)
        {
            ++_performanceVersion; CashierPerformanceReport.Clear();
            FeedbackMessage = "La comisión debe estar entre 0 y 100%, con hasta un decimal.";
            return;
        }
        _ = SaveCommissionAsync(value);
        _ = LoadCashierPerformanceAsync();
    }

    private async Task SaveCommissionAsync(decimal value)
    {
        if (_settingsService == null) return;
        try { await _settingsService.SetAsync(CommissionKey, value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception ex) { Log.Warning(ex, "Could not persist commission percentage"); }
    }

    public event Action? OpenCashupRequested;
    public event Action<SaleDto>? OpenReturnRequested;
    public event Action<string, Action<bool>>? OpenSupervisorPinRequested;

    public HistoryViewModel(ISaleService saleService, IEscPosPrinterService printerService, ISettingsService? settingsService = null, IShiftService? shiftService = null, bool loadOnCreate = true)
    {
        _saleService = saleService;
        _printerService = printerService;
        _shiftService = shiftService;
        _settingsService = settingsService;
        _acceptEventRefresh = loadOnCreate;

        _saleService.SaleSaved += OnSaleSaved;
        WeakReferenceMessenger.Default.Register<Ticketfy.Core.Messages.CashupSavedMessage>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(() => { if (_acceptEventRefresh) _ = FetchSalesHistoryAsync(); });
        });

        if (loadOnCreate)
        {
            _ = LoadSavedCommissionAsync();
            _ = LoadSalesAsync();
            _ = LoadCashierPerformanceAsync();
        }
    }

    private void OnSaleSaved(SaleDto sale)
    {
        if (_acceptEventRefresh) _ = FetchSalesHistoryAsync();
    }

    public async Task LoadSavedCommissionAsync()
    {
        var version = ++_settingsVersion;
        if (_settingsService == null) return;
        try
        {
            var saved = await _settingsService.GetAsync(CommissionKey);
            if (version != _settingsVersion) return;
            if (!string.IsNullOrEmpty(saved) && decimal.TryParse(saved, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal v)
                && v >= 0m && v <= 100m && Math.Round(v, 1) == v)
                SetLoadedCommission(v);
            else
                SetLoadedCommission(0m);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not load saved commission percentage"); }
    }

    public async Task LoadSalesAsync()
    {
        _acceptEventRefresh = true;
        var expectedVersion = _salesVersion + 1;
        await LoadSalesPageAsync();
        if (expectedVersion == _salesVersion) await LoadCashupsAsync();
    }

    [RelayCommand]
    private async Task FetchSalesHistoryAsync()
    {
        _salesPage = 0; _cashupPage = 0;
        var expectedVersion = _salesVersion + 1;
        await LoadSalesAsync();
        if (expectedVersion == _salesVersion) await LoadCashierPerformanceAsync();
    }

    private async Task LoadSalesPageAsync()
    {
        var version = ++_salesVersion;
        IsLoading = true;
        try
        {
            DateTime queryStart = StartDate?.Date ?? Ticketfy.Core.Helpers.BusinessTime.Today;
            DateTime queryEnd = (EndDate?.Date ?? Ticketfy.Core.Helpers.BusinessTime.Today).AddTicks(TimeSpan.TicksPerDay - 1);

            var result = await _saleService.GetHistoryPageAsync(queryStart, queryEnd, _salesPage);
            if (version != _salesVersion) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _salesVersion) return;
                Sales.Clear();
                foreach (var sale in result.Items)
                {
                    Sales.Add(sale);
                }

                MatchingSalesCount = result.TotalCount; HasMoreSales = result.HasMore;
                ApplyHourlySummary(result.Hours);
                NotifySalesPage();
                FeedbackMessage = $"{result.TotalCount} ventas en el período. Resumen de todo el período; documentos paginados de 100 en 100.";
                if (result.Items.Any(s => !s.DetailsAvailable)) FeedbackMessage += " Hay desgloses pendientes o no verificables; abra Ver detalle antes de imprimir o devolver.";
                if (result.UnparseableDateCount > 0)
                    FeedbackMessage += $" Atención: {result.UnparseableDateCount} documentos con fecha no interpretable requieren conciliación histórica y no se asignaron a este período.";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching date-filtered sales history");
            if (version != _salesVersion) return;
            Sales.Clear(); CloseSaleDetail();
            HasMoreSales = false; MatchingSalesCount = 0; NotifySalesPage();
            ApplyHourlySummary([]);
            FeedbackMessage = "No se pudo consultar el historial. Verifique permisos y vuelva a consultar.";
        }
        finally
        {
            if (version == _salesVersion) IsLoading = false;
        }
    }

    public async Task LoadCashupsAsync()
    {
        var version = ++_cashupsVersion;
        IsLoadingCashups = true;
        try
        {
            DateTime start = StartDate?.Date ?? BusinessTime.Today;
            DateTime end = EndDate?.Date ?? BusinessTime.Today;
            var result = _shiftService is null ? new CashupHistoryPage([], 0, false, 0)
                : await _shiftService.GetCashupPageAsync(start, end, _cashupPage);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _cashupsVersion) return;
                Cashups.Clear(); CloseCashupDetail();
                foreach (var cashup in result.Items) Cashups.Add(cashup);
                MatchingCashupCount = result.TotalCount; HasMoreCashups = result.HasMore;
                NotifyCashupPage();
                CashupFeedbackMessage = $"{result.TotalCount} cortes en el período. Los desgloses corresponden al instante de cada corte.";
                if (result.Items.Any(c => !c.TotalSales.HasValue || !c.TotalExpenses.HasValue))
                    CashupFeedbackMessage += " Hay cortes sin desglose verificable; revise su conciliación histórica.";
                if (result.UnparseableDateCount > 0)
                    CashupFeedbackMessage += $" {result.UnparseableDateCount} cortes con fecha no interpretable no se asignaron al período.";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching cashup history");
            if (version != _cashupsVersion) return;
            Cashups.Clear(); CloseCashupDetail(); MatchingCashupCount = 0; HasMoreCashups = false; NotifyCashupPage();
            CashupFeedbackMessage = "No se pudieron consultar los cortes. Verifique permisos y vuelva a consultar.";
        }
        finally { if (version == _cashupsVersion) IsLoadingCashups = false; }
    }

    [RelayCommand]
    public async Task LoadCashierPerformanceAsync()
    {
        var version = ++_performanceVersion;
        try
        {
            if (CommissionPercentage < 0m || CommissionPercentage > 100m || Math.Round(CommissionPercentage, 1) != CommissionPercentage)
                throw new InvalidOperationException("Porcentaje de comisión inválido.");
            DateTime queryStart = StartDate?.Date ?? Ticketfy.Core.Helpers.BusinessTime.Today;
            DateTime queryEnd = (EndDate?.Date ?? Ticketfy.Core.Helpers.BusinessTime.Today).AddTicks(TimeSpan.TicksPerDay - 1);

            var list = await _saleService.GetCashierPerformanceReportAsync(queryStart, queryEnd, CommissionPercentage);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _performanceVersion) return;
                CashierPerformanceReport.Clear();
                foreach (var c in list) CashierPerformanceReport.Add(c);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading cashier performance");
            if (version == _performanceVersion) CashierPerformanceReport.Clear();
        }
    }

    private void ApplyHourlySummary(List<HourlySaleTotal> hours)
    {
        HourlyReport.Clear();
        // Ticket issuance volume, not cash flow: compensations are reported separately in financial reports.
        TotalDayRevenue = hours.Sum(h => h.GrossAmount);

        decimal maxRev = 0m;
        int peakHour = -1;
        PeakHourLabel = "Ninguna";
        PeakHourRevenue = 0m;

        foreach (var hour in hours)
        {
            var sum = hour.GrossAmount;
            if (sum > maxRev)
            {
                maxRev = sum;
                peakHour = hour.Hour;
            }
        }

        if (peakHour >= 0)
        {
            PeakHourLabel = $"{peakHour:D2}:00 - {(peakHour + 1):D2}:00";
            PeakHourRevenue = maxRev;
        }

        foreach (var hour in hours)
        {
            var h = hour.Hour;
            var isPeak = h == peakHour;
            HourlyReport.Add(new HourlySalesDto($"{h:D2}:00 - {(h + 1):D2}:00", hour.TicketCount, hour.GrossAmount, isPeak));
        }
    }

    [RelayCommand]
    private async Task RePrintTicketAsync(SaleDto sale)
    {
        if (sale == null) return;
        var version = _salesVersion;

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow == null)
        {
            FeedbackMessage = "No se pudo abrir la vista previa. El ticket no se envió a impresión.";
            return;
        }

        var vm = new Ticketfy.ViewModels.Dialogs.PrintPreviewWindowViewModel($"Reimpresión de Ticket #{sale.Id}");
        var win = new Ticketfy.Views.Dialogs.PrintPreviewWindow { DataContext = vm };

        var confirmed = await win.ShowDialog<bool>(desktop.MainWindow);
        if (!confirmed || version != _salesVersion) return;

        try
        {
            var detail = await _saleService.GetByIdAsync(sale.Id);
            if (version != _salesVersion) return;
            if (detail is null || !detail.DetailsAvailable)
            {
                FeedbackMessage = detail?.DetailWarning ?? "Venta no disponible; no se envió a impresión.";
                return;
            }
            var success = await _printerService.PrintTicketAsync(detail);
            if (version != _salesVersion) return;
            FeedbackMessage = success ? "Ticket enviado al controlador; compruebe la salida física." : "Envío no confirmado. Revise la impresora antes de reintentar.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reprinting ticket");
            if (version != _salesVersion) return;
            FeedbackMessage = "Error al imprimir ticket";
        }
    }

    [RelayCommand]
    private async Task CancelSaleAsync(SaleDto sale)
    {
        if (sale == null || sale.IsCancelled) return;
        var version = _salesVersion;
        var responded = false;

        OpenSupervisorPinRequested?.Invoke($"Cancelar Venta Folio: {sale.Id.Substring(0, Math.Min(8, sale.Id.Length))}", async (authorized) =>
        {
            if (responded || version != _salesVersion) return;
            responded = true;
            if (!authorized)
            {
                FeedbackMessage = "Cancelación no autorizada por el supervisor.";
                return;
            }

            try
            {
                var canceled = await _saleService.CancelSaleAsync(sale.Id, "Cancelación administrativa");
                if (version != _salesVersion) return;
                if (!canceled)
                {
                    FeedbackMessage = "No se registró una nueva cancelación. Consulte el folio y sus devoluciones; no repita la entrega de dinero ni de mercancía.";
                    return;
                }
                // Refresh is a read after the commit, never evidence that a cancellation failed.
                var refreshVersion = _salesVersion + 1;
                await FetchSalesHistoryAsync();
                if (refreshVersion != _salesVersion) return;
                FeedbackMessage = "Cancelación registrada. No repita la entrega de dinero ni de mercancía. " +
                    "Los reembolsos de tarjeta o transferencia requieren conciliación bancaria. " + FeedbackMessage;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error cancelling sale");
                if (version != _salesVersion) return;
                FeedbackMessage = "No se pudo confirmar la cancelación. Conserve el folio y solicite conciliación; no entregue dinero ni mueva mercancía.";
            }
        });
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenReturnDialog(SaleDto sale)
    {
        if (sale == null || sale.IsCancelled) return;
        var version = _salesVersion;

        var responded = false;
        OpenSupervisorPinRequested?.Invoke($"Autorizar Devolución Folio: {sale.Id.Substring(0, Math.Min(8, sale.Id.Length))}", async (authorized) =>
        {
            if (responded || version != _salesVersion) return;
            responded = true;
            if (authorized)
            {
                try
                {
                    var detail = await _saleService.GetByIdAsync(sale.Id);
                    if (version != _salesVersion) return;
                    if (detail is null || !detail.DetailsAvailable)
                        FeedbackMessage = detail?.DetailWarning ?? "Venta no disponible para devolución.";
                    else OpenReturnRequested?.Invoke(detail);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading sale for return");
                    if (version == _salesVersion) FeedbackMessage = "No se pudo verificar el detalle; no entregue dinero ni mueva mercancía.";
                }
            }
            else
            {
                FeedbackMessage = "Autorización denegada. PIN de administrador incorrecto.";
            }
        });
    }

    [RelayCommand]
    private void PerformCashCutoff() => OpenCashupRequested?.Invoke();

    [ObservableProperty] private Ticketfy.Data.Entities.CashupEntity? _selectedCashupForDetail;
    [ObservableProperty] private bool _isCashupDetailDialogOpen = false;

    [ObservableProperty] private SaleDto? _selectedSaleForDetail;
    [ObservableProperty] private bool _isSaleDetailDialogOpen = false;

    [RelayCommand]
    private async Task ViewSaleDetailAsync(SaleDto? sale)
    {
        if (sale == null) return;
        var version = _salesVersion; var detailVersion = ++_detailVersion;
        SelectedSaleForDetail = null; IsSaleDetailDialogOpen = false;
        try
        {
            var detail = await _saleService.GetByIdAsync(sale.Id);
            if (version != _salesVersion || detailVersion != _detailVersion) return;
            SelectedSaleForDetail = detail; IsSaleDetailDialogOpen = detail is not null;
            if (detail is null) FeedbackMessage = "Venta no disponible.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reading sale detail");
            if (version == _salesVersion && detailVersion == _detailVersion) FeedbackMessage = "No se pudo consultar el detalle. No imprima ni devuelva mercancía con datos anteriores.";
        }
    }

    [RelayCommand]
    private void CloseSaleDetail()
    {
        ++_detailVersion;
        IsSaleDetailDialogOpen = false;
        SelectedSaleForDetail = null;
    }

    [RelayCommand]
    private void ViewCashupDetail(Ticketfy.Data.Entities.CashupEntity? cashup)
    {
        if (cashup == null) return;
        SelectedCashupForDetail = cashup;
        IsCashupDetailDialogOpen = true;
    }

    [RelayCommand]
    private void CloseCashupDetail()
    {
        IsCashupDetailDialogOpen = false;
        SelectedCashupForDetail = null;
    }

    [RelayCommand]
    private async Task PrintCashupTicketAsync(Ticketfy.Data.Entities.CashupEntity? cashup)
    {
        if (cashup == null || _printerService == null) return;
        var version = _cashupsVersion;
        if (!BusinessTime.TryLocal(cashup.Timestamp, out var timestamp))
        {
            FeedbackMessage = "La fecha histórica del corte no es interpretable. Concilie el documento antes de reimprimirlo.";
            return;
        }
        try
        {
            var slipModel = new Ticketfy.Core.Models.ShiftMovementSlipModel
            {
                Folio = $"CORTE-{cashup.Id.Substring(0, Math.Min(8, cashup.Id.Length)).ToUpper()}",
                Timestamp = timestamp,
                CashierName = cashup.CashierName,
                MovementTypeLabel = "REPORTE DE CORTE DE CAJA Y TURNO",
                Description = $"Fondo Inicial: ${cashup.OpenCashAmount:N2}\nVentas en efectivo: {cashup.TotalSales?.ToString("C2") ?? "Sin desglose histórico"}\nEgresos de efectivo: {cashup.TotalExpenses?.ToString("C2") ?? "Sin desglose histórico"}\nTotal Físico: ${cashup.ClosedCashAmount:N2}\nDiferencia: ${cashup.Difference:N2}\nNotas: {cashup.Notes}",
                Amount = cashup.ClosedCashAmount
            };
            bool sent = await _printerService.PrintNonSaleCashMovementSlipAsync(slipModel);
            if (version != _cashupsVersion) return;
            FeedbackMessage = sent ? "Reporte enviado al controlador; compruebe el papel." : "No se confirmó el envío del corte; revise la impresora.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reprinting cashup report ticket");
            if (version != _cashupsVersion) return;
            FeedbackMessage = "Error al reimprimir reporte de corte.";
        }
    }
}
