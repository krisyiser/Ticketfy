using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Inventory;

/// <summary>
/// Manages predictive restock alerts and intelligent inventory insights.
/// Extracted from InventoryViewModel.
/// </summary>
public partial class InventoryIntelligenceViewModel : ObservableObject
{
    private readonly IPredictiveIntelligenceService? _predictiveService;
    private long _loadVersion;
    private CancellationTokenSource? _loading;
    public void ClearSessionData()
    {
        ++_loadVersion; _loading?.Cancel(); _loading = null;
        UrgentRestockAlerts.Clear(); IsIntelligencePanelVisible = false; Status = "";
    }

    public ObservableCollection<PredictiveAlertDto> UrgentRestockAlerts { get; } = [];

    [ObservableProperty] private bool _isIntelligencePanelVisible = false;
    [ObservableProperty] private string _status = "";

    public InventoryIntelligenceViewModel(IPredictiveIntelligenceService? predictiveService = null)
    {
        _predictiveService = predictiveService;
    }

    public async Task RefreshAlertsAsync()
    {
        ClearSessionData();
        var version = _loadVersion;
        if (_predictiveService == null) return;
        var source = new CancellationTokenSource();
        _loading = source;
        Status = "Consultando consumo e inventario...";
        IsIntelligencePanelVisible = true;
        try
        {
            var alerts = await Task.Run(() => _predictiveService.GetUrgentRestockAlertsAsync(source.Token), source.Token);
            if (version != _loadVersion || source.IsCancellationRequested) return;
            UrgentRestockAlerts.Clear();
            foreach (var alert in alerts.Take(3)) UrgentRestockAlerts.Add(alert);
            Status = alerts.Count > 3 ? $"Mostrando las 3 estimaciones más urgentes de {alerts.Count} seleccionadas (máximo 20)." :
                alerts.Count > 0 ? "Estimación de consumo físico neto de 28 días; no es una orden de compra." : "";
            IsIntelligencePanelVisible = UrgentRestockAlerts.Count > 0;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex, "InventoryIntelligenceViewModel: error refreshing alerts");
            if (version == _loadVersion)
            {
                UrgentRestockAlerts.Clear();
                Status = "Estimación no disponible. Actualice el inventario para reintentar; no significa que no haya faltantes.";
                IsIntelligencePanelVisible = true;
            }
        }
        finally { if (ReferenceEquals(_loading, source)) _loading = null; source.Dispose(); }
    }
}
