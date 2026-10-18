using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Models;
using Ticketfy.Services.Interfaces;
using Serilog;

namespace Ticketfy.ViewModels;

public partial class CashierPerformanceViewModel : ObservableObject
{
    private readonly IPerformanceAnalyticsService _performanceService;
    private readonly IAttendanceService? _attendanceService;

    public ObservableCollection<CashierProductivityReportModel> CashierProductivityReports { get; } = [];

    [ObservableProperty] private DateTimeOffset? _startDate = DateTimeOffset.Now.AddDays(-7);
    [ObservableProperty] private DateTimeOffset? _endDate = DateTimeOffset.Now;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private decimal _commissionPercentage = 0m;

    partial void OnCommissionPercentageChanged(decimal value)
    {
        foreach (var report in CashierProductivityReports)
        {
            report.EstimatedCommission = Math.Round(report.GrossSales * (value / 100m), 2);
        }
    }

    public CashierPerformanceViewModel(IPerformanceAnalyticsService performanceService, IAttendanceService? attendanceService = null)
    {
        _performanceService = performanceService;
        _attendanceService = attendanceService;
        _ = LoadReportsAsync();
    }

    [RelayCommand]
    public async Task LoadReportsAsync()
    {
        IsLoading = true;
        try
        {
            if (_attendanceService != null)
            {
                await _attendanceService.AutoCloseForgottenAttendancesAsync();
            }

            var start = StartDate?.DateTime ?? DateTime.Today.AddDays(-7);
            var end = EndDate?.DateTime ?? DateTime.Today;
            var reports = await _performanceService.CalculateTrueCashierProductivityAsync(start, end);
            CashierProductivityReports.Clear();
            foreach (var r in reports)
            {
                r.EstimatedCommission = Math.Round(r.GrossSales * (CommissionPercentage / 100m), 2);
                CashierProductivityReports.Add(r);
            }
            FeedbackMessage = $"Reporte cargado: {CashierProductivityReports.Count} cajeros analizados";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading Cashier Productivity Reports");
            FeedbackMessage = "Error al calcular la productividad de cajeros";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
