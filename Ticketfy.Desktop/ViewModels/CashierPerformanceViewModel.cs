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
    private long _loadVersion;
    public void ClearSessionData()
    {
        ++_loadVersion; CashierProductivityReports.Clear(); IsLoading = false;
        FeedbackMessage = string.Empty; CommissionPercentage = 0m;
    }

    public ObservableCollection<CashierProductivityReportModel> CashierProductivityReports { get; } = [];

    [ObservableProperty] private DateTimeOffset? _startDate = new DateTimeOffset(Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(-7), TimeSpan.Zero);
    [ObservableProperty] private DateTimeOffset? _endDate = new DateTimeOffset(Ticketfy.Core.Helpers.BusinessTime.Today, TimeSpan.Zero);
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private decimal _commissionPercentage = 0m;
    private bool CommissionValid => CommissionPercentage >= 0 && CommissionPercentage <= 100
        && Math.Round(CommissionPercentage, 1) == CommissionPercentage;
    partial void OnStartDateChanged(DateTimeOffset? value) => InvalidatePeriod();
    partial void OnEndDateChanged(DateTimeOffset? value) => InvalidatePeriod();
    private void InvalidatePeriod()
    {
        ++_loadVersion; CashierProductivityReports.Clear(); IsLoading = false;
        FeedbackMessage = "Fechas modificadas; pulse Filtrar para consultar el período.";
    }

    partial void OnCommissionPercentageChanged(decimal value)
    {
        foreach (var report in CashierProductivityReports)
        {
            report.EstimatedCommission = CommissionValid && report.AttributionVerified ? Math.Round(report.GrossSales * (value / 100m), 2) : 0m;
        }
        if (!CommissionValid) FeedbackMessage = "La comisión debe estar entre 0 y 100%, con hasta un decimal.";
    }

    public CashierPerformanceViewModel(IPerformanceAnalyticsService performanceService, IAttendanceService? attendanceService = null, bool loadOnCreate = true)
    {
        _performanceService = performanceService;
        if (loadOnCreate) _ = LoadReportsAsync();
    }

    [RelayCommand]
    public async Task LoadReportsAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            if (!CommissionValid) throw new InvalidOperationException("Porcentaje de comisión inválido.");
            var start = StartDate?.Date ?? Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(-7);
            var end = EndDate?.Date ?? Ticketfy.Core.Helpers.BusinessTime.Today;
            var reports = await _performanceService.CalculateTrueCashierProductivityAsync(start, end);
            if (version != _loadVersion) return;
            CashierProductivityReports.Clear();
            foreach (var r in reports)
            {
                r.EstimatedCommission = r.AttributionVerified ? Math.Round(r.GrossSales * (CommissionPercentage / 100m), 2) : 0m;
                CashierProductivityReports.Add(r);
            }
            FeedbackMessage = $"Reporte cargado: {reports.Count(r => r.AttributionVerified)} usuarios. Venta neta de devoluciones; comisiones estimadas, no nómina.";
            if (reports.Any(r => !r.AttributionVerified)) FeedbackMessage += " Los importes sin usuario verificable están separados y no generan comisión atribuida.";
            if (reports.Any(r => r.AttendanceIssueCount > 0)) FeedbackMessage += " Hay asistencias por revisar; sus tasas por hora no son calculables.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading Cashier Productivity Reports");
            if (version != _loadVersion) return;
            CashierProductivityReports.Clear();
            FeedbackMessage = "Error al calcular la productividad de cajeros";
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }
}
