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
    private readonly Ticketfy.Data.AppDbContext _db;
    private readonly ISettingsService? _settingsService;

    private const string CommissionKey = "HistoryCommissionPercentage";

    public ObservableCollection<SaleDto> Sales { get; } = [];
    public ObservableCollection<HourlySalesDto> HourlyReport { get; } = [];
    public ObservableCollection<CashierPerformanceDto> CashierPerformanceReport { get; } = [];
    public ObservableCollection<Ticketfy.Data.Entities.CashupEntity> Cashups { get; } = [];

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private string _peakHourLabel = "Ninguna";
    [ObservableProperty] private decimal _peakHourRevenue;
    [ObservableProperty] private decimal _totalDayRevenue;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private decimal _commissionPercentage = 0m;

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

    public HistoryViewModel(ISaleService saleService, IEscPosPrinterService printerService, Ticketfy.Data.AppDbContext db, ISettingsService? settingsService = null)
    {
        _saleService = saleService;
        _printerService = printerService;
        _db = db;
        _settingsService = settingsService;

        _saleService.SaleSaved += OnSaleSaved;
        WeakReferenceMessenger.Default.Register<Ticketfy.Core.Messages.CashupSavedMessage>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(() => _ = FetchSalesHistoryAsync());
        });

        _ = LoadSavedCommissionAsync();
        _ = LoadSalesAsync();
        _ = LoadCashierPerformanceAsync();
    }

    private static bool TryParseCashupDate(string? timestampStr, out DateTime dt)
    {
        dt = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(timestampStr)) return false;

        if (DateTime.TryParse(timestampStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt))
            return true;
        if (DateTime.TryParse(timestampStr, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out dt))
            return true;
        if (DateTime.TryParse(timestampStr, out dt))
            return true;

        return false;
    }

    private void OnSaleSaved(SaleDto sale)
    {
        _ = FetchSalesHistoryAsync();
    }

    private async Task LoadSavedCommissionAsync()
    {
        if (_settingsService == null) return;
        try
        {
            var saved = await _settingsService.GetAsync(CommissionKey);
            if (!string.IsNullOrEmpty(saved) && decimal.TryParse(saved, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal v) && v >= 0m)
                CommissionPercentage = v;
            else
                CommissionPercentage = 0m;
        }
        catch (Exception ex) { Log.Warning(ex, "Could not load saved commission percentage"); }
    }

    public Task LoadSalesAsync() => FetchSalesHistoryAsync();

    [RelayCommand]
    private async Task FetchSalesHistoryAsync()
    {
        IsLoading = true;
        try
        {
            DateTime queryStart = StartDate?.Date ?? DateTime.Today;
            DateTime queryEnd = EndDate?.Date.AddDays(1).AddTicks(-1) ?? DateTime.Today.AddDays(1).AddTicks(-1);

            var salesList = await _saleService.GetSalesByDateRangeAsync(queryStart, queryEnd);
            if (salesList.Count == 0 && StartDate == DateTime.Today && EndDate == DateTime.Today)
            {
                salesList = await _saleService.GetHistoryAsync(500);
            }

            List<Ticketfy.Data.Entities.CashupEntity> cashupsQuery = [];
            try
            {
                var allCashups = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(_db.Cashups);
                cashupsQuery = allCashups
                    .Where(c =>
                    {
                        if (TryParseCashupDate(c.Timestamp, out var dt))
                        {
                            return dt >= queryStart && dt <= queryEnd;
                        }
                        return true;
                    })
                    .OrderByDescending(c =>
                    {
                        TryParseCashupDate(c.Timestamp, out var dt);
                        return dt;
                    })
                    .ToList();

                if (cashupsQuery.Count == 0 && StartDate == DateTime.Today && EndDate == DateTime.Today)
                {
                    cashupsQuery = allCashups
                        .OrderByDescending(c =>
                        {
                            TryParseCashupDate(c.Timestamp, out var dt);
                            return dt;
                        })
                        .Take(500)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not fetch cashups list in HistoryViewModel");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Sales.Clear();
                foreach (var sale in salesList.OrderByDescending(s => s.Date).Take(500))
                {
                    Sales.Add(sale);
                }

                Cashups.Clear();
                foreach (var cashup in cashupsQuery)
                {
                    Cashups.Add(cashup);
                }

                CalculateHourlyReport(Sales.ToList());
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fetching date-filtered sales history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task LoadCashierPerformanceAsync()
    {
        try
        {
            DateTime? queryStart = StartDate?.Date;
            DateTime? queryEnd = EndDate?.Date.AddDays(1).AddTicks(-1);

            var list = await _saleService.GetCashierPerformanceReportAsync(queryStart, queryEnd, CommissionPercentage);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CashierPerformanceReport.Clear();
                foreach (var c in list) CashierPerformanceReport.Add(c);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading cashier performance");
        }
    }

    private void CalculateHourlyReport(List<SaleDto> salesList)
    {
        HourlyReport.Clear();
        var validSales = salesList.Where(s => !s.IsCancelled).ToList();
        TotalDayRevenue = validSales.Sum(s => s.Total);

        var groups = validSales
            .GroupBy(s =>
            {
                if (DateTimeOffset.TryParse(s.Date, out var dto))
                {
                    return dto.LocalDateTime.Hour;
                }
                if (DateTime.TryParse(s.Date, out var dt))
                {
                    return dt.ToLocalTime().Hour;
                }
                return 0;
            })
            .OrderBy(g => g.Key)
            .ToList();

        decimal maxRev = 0m;
        int peakHour = -1;

        foreach (var g in groups)
        {
            var sum = g.Sum(s => s.Total);
            if (sum > maxRev)
            {
                maxRev = sum;
                peakHour = g.Key;
            }
        }

        if (peakHour >= 0)
        {
            PeakHourLabel = $"{peakHour:D2}:00 - {(peakHour + 1):D2}:00";
            PeakHourRevenue = maxRev;
        }

        foreach (var g in groups)
        {
            var h = g.Key;
            var isPeak = h == peakHour;
            HourlyReport.Add(new HourlySalesDto($"{h:D2}:00 - {(h + 1):D2}:00", g.Count(), g.Sum(s => s.Total), isPeak));
        }
    }

    [RelayCommand]
    private async Task RePrintTicketAsync(SaleDto sale)
    {
        if (sale == null) return;

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow == null) return;

        var vm = new Ticketfy.ViewModels.Dialogs.PrintPreviewWindowViewModel($"Reimpresión de Ticket #{sale.Id}");
        var win = new Ticketfy.Views.Dialogs.PrintPreviewWindow { DataContext = vm };

        var confirmed = await win.ShowDialog<bool>(desktop.MainWindow);
        if (!confirmed) return;

        try
        {
            var success = await _printerService.PrintTicketAsync(sale, "COM1");
            FeedbackMessage = success ? "¡Ticket enviado a la impresora térmica ESC/POS!" : "Error enviando a la impresora";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reprinting ticket");
            FeedbackMessage = "Error al imprimir ticket";
        }
    }

    [RelayCommand]
    private async Task CancelSaleAsync(SaleDto sale)
    {
        if (sale == null || sale.IsCancelled) return;

        OpenSupervisorPinRequested?.Invoke($"Cancelar Venta Folio: {sale.Id.Substring(0, Math.Min(8, sale.Id.Length))}", async (authorized) =>
        {
            if (!authorized)
            {
                FeedbackMessage = "Autorización denegada. PIN de administrador incorrecto.";
                return;
            }

            try
            {
                await _saleService.CancelAsync(sale.Id);
                FeedbackMessage = "Venta cancelada e inventario restablecido";
                await LoadSalesAsync();
                await LoadCashierPerformanceAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error cancelling sale");
                FeedbackMessage = "Error al cancelar la venta";
            }
        });
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenReturnDialog(SaleDto sale)
    {
        if (sale == null || sale.IsCancelled) return;

        OpenSupervisorPinRequested?.Invoke($"Autorizar Devolución Folio: {sale.Id.Substring(0, Math.Min(8, sale.Id.Length))}", (authorized) =>
        {
            if (authorized)
            {
                OpenReturnRequested?.Invoke(sale);
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
    private void ViewSaleDetail(SaleDto? sale)
    {
        if (sale == null) return;
        SelectedSaleForDetail = sale;
        IsSaleDetailDialogOpen = true;
    }

    [RelayCommand]
    private void CloseSaleDetail()
    {
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
        try
        {
            var slipModel = new Ticketfy.Core.Models.ShiftMovementSlipModel
            {
                Folio = $"CORTE-{cashup.Id.Substring(0, Math.Min(8, cashup.Id.Length)).ToUpper()}",
                Timestamp = DateTime.TryParse(cashup.Timestamp, out var dt) ? dt : DateTime.Now,
                CashierName = "CAJERO / SUPERVISOR",
                MovementTypeLabel = "REPORTE DE CORTE DE CAJA Y TURNO",
                Description = $"Fondo Inicial: ${cashup.OpenCashAmount:N2}\nVentas: ${cashup.TotalSales:N2}\nTotal Físico: ${cashup.ClosedCashAmount:N2}\nDiferencia: ${cashup.Difference:N2}\nNotas: {cashup.Notes}",
                Amount = cashup.ClosedCashAmount
            };
            await _printerService.PrintNonSaleCashMovementSlipAsync(slipModel, "ImpresoraTickets");
            FeedbackMessage = "Reporte de corte enviado a impresora.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reprinting cashup report ticket");
            FeedbackMessage = "Error al reimprimir reporte de corte.";
        }
    }
}
