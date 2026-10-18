using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Helpers;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.History;

/// <summary>
/// Manages sales transaction history listing, date filtering, and hourly traffic reports.
/// Extracted from HistoryViewModel.
/// </summary>
public partial class SalesHistoryViewModel : ObservableObject
{
    private readonly ISaleService _saleService;
    private readonly IEscPosPrinterService _printerService;

    public ObservableCollection<SaleDto> Sales { get; } = [];
    public ObservableCollection<HourlySalesDto> HourlyReport { get; } = [];

    [ObservableProperty] private DateTime? _startDate = DateTime.Today;
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _peakHourLabel = "Ninguna";
    [ObservableProperty] private decimal _peakHourRevenue;
    [ObservableProperty] private decimal _totalDayRevenue;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public event Action<SaleDto>? OpenReturnRequested;

    public SalesHistoryViewModel(ISaleService saleService, IEscPosPrinterService printerService)
    {
        _saleService = saleService;
        _printerService = printerService;
    }

    public async Task FetchSalesHistoryAsync()
    {
        if (!StartDate.HasValue || !EndDate.HasValue) return;

        IsLoading = true;
        try
        {
            DateTime queryStart = StartDate.Value.Date;
            DateTime queryEnd = EndDate.Value.Date.AddDays(1).AddTicks(-1);

            var salesList = await _saleService.GetSalesByDateRangeAsync(queryStart, queryEnd);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Sales.Clear();
                foreach (var sale in salesList.OrderByDescending(s => s.Date).Take(500))
                {
                    Sales.Add(sale);
                }

                CalculateHourlyReport(Sales.ToList());
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SalesHistoryViewModel: error fetching sales history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void CalculateHourlyReport(System.Collections.Generic.List<SaleDto> salesList)
    {
        HourlyReport.Clear();
        var hourlyGroups = salesList
            .GroupBy(s =>
            {
                if (DateTime.TryParse(s.Date, out var dt)) return dt.ToLocalTime().Hour;
                return 0;
            })
            .ToDictionary(g => g.Key, g => new { Count = g.Count(), Revenue = g.Sum(x => x.Total) });

        decimal maxRev = 0m;
        int peakHour = -1;

        for (int h = 0; h < 24; h++)
        {
            int count = hourlyGroups.ContainsKey(h) ? hourlyGroups[h].Count : 0;
            decimal rev = hourlyGroups.ContainsKey(h) ? hourlyGroups[h].Revenue : 0m;

            if (rev > maxRev)
            {
                maxRev = rev;
                peakHour = h;
            }

            HourlyReport.Add(new HourlySalesDto($"{h:00}:00 - {h:00}:59", count, rev, false));
        }

        if (peakHour >= 0 && maxRev > 0)
        {
            PeakHourLabel = $"{peakHour:00}:00 - {peakHour:00}:59";
            PeakHourRevenue = maxRev;
            var match = HourlyReport.FirstOrDefault(x => x.HourLabel.StartsWith($"{peakHour:00}:00"));
            if (match != null)
            {
                int index = HourlyReport.IndexOf(match);
                HourlyReport[index] = match with { IsPeakHour = true };
            }
        }
        else
        {
            PeakHourLabel = "Ninguna";
            PeakHourRevenue = 0m;
        }

        TotalDayRevenue = salesList.Sum(s => s.Total);
    }

    [RelayCommand]
    private void OpenReturn(SaleDto sale) => OpenReturnRequested?.Invoke(sale);
}
