using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Ticketfy.Core.Helpers;

namespace Ticketfy.ViewModels;

public partial class ExpensesViewModel
{
    private int _page;
    private bool _cashVisible, _hasActiveShift;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNextPage), nameof(CanLoadPreviousPage), nameof(PageStatus))]
    private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNextPage), nameof(CanLoadPreviousPage), nameof(PageStatus))]
    [NotifyPropertyChangedFor(nameof(OpeningBalanceDisplay), nameof(CashBalanceDisplay))]
    [NotifyPropertyChangedFor(nameof(CancellationAmountDisplay))]
    private bool _hasLoadedData;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadNextPage))]
    private bool _hasMore;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    private int _totalCount;
    [ObservableProperty] private bool _financialVisible;
    [ObservableProperty] private string _readMessage = "Sin consulta disponible.";
    public bool CanLoadNextPage => HasLoadedData && !IsLoading && HasMore;
    public bool CanLoadPreviousPage => HasLoadedData && !IsLoading && _page > 0;
    public string PageStatus => IsLoading ? "Consultando..." : !HasLoadedData ? "Sin consulta disponible"
        : ShowingCashMovements ? $"Página {_page + 1} · {CashMovements.Count} de {TotalCount} movimientos"
        : ShowingCancellations ? $"Página {_page + 1} · {Cancellations.Count} de {TotalCount} cancelaciones"
        : $"Página {_page + 1} · {Expenses.Count} de {TotalCount} gastos";
    public string OpeningBalanceDisplay => !HasLoadedData ? "No disponible" : !_cashVisible ? "Restringido" : !_hasActiveShift ? "Sin turno"
        : SelectedPeriod != "Turno Activo" ? "No aplica al período" : $"{FondoInicial:C2}";
    public string CashBalanceDisplay => !HasLoadedData ? "No disponible" : !_cashVisible ? "Restringido" : !_hasActiveShift ? "Sin turno" : $"{TotalEnCaja:C2}";

    [RelayCommand] private Task RefreshExpensesAsync() => LoadExpensesAsync();
    [RelayCommand]
    private async Task NextPageAsync() { if (CanLoadNextPage) await LoadPageAsync(_page + 1); }
    [RelayCommand]
    private async Task PreviousPageAsync() { if (CanLoadPreviousPage) await LoadPageAsync(_page - 1); }
    public async Task LoadExpensesAsync() => await LoadPageAsync(0);

    private async Task<bool> LoadPageAsync(int requestedPage)
    {
        int version = ++_loadVersion;
        string selected = SelectedPeriod;
        bool cancellations = ShowingCancellations;
        _page = requestedPage;
        ClearReadData();
        CashMovements.Clear();
        IsLoading = true;
        try
        {
            if (ShowingCashMovements)
            {
                if (_shiftService is null) return false;
                var movements = await _shiftService.GetManualMovementPageAsync(requestedPage);
                if (version != _loadVersion) return false;
                foreach (var row in movements.Items) CashMovements.Add(row);
                TotalCount = movements.TotalCount; HasMore = movements.HasMore; HasLoadedData = true;
                PeriodSubtitleDisplay = "Ingresos y retiros del turno actual";
                ReadMessage = "Movimientos de efectivo del turno actual. Los ingresos y retiros no se contabilizan como ventas ni gastos operativos.";
                OnPropertyChanged(nameof(PageStatus));
                return true;
            }
            var now = BusinessTime.Now;
            bool byShift = selected == "Turno Activo";
            DateTime? start = selected switch
            {
                "Hoy" => now.Date,
                "Esta Semana" => now.Date.AddDays(-((7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7)),
                "Este Mes" => new DateTime(now.Year, now.Month, 1),
                "Turno Activo" or "Todo el Histórico" => null,
                _ => throw new InvalidOperationException("Seleccione un período válido.")
            };
            DateTime? end = byShift || selected == "Todo el Histórico" ? null : now;
            if (!HistoryOptions.Contains(SelectedHistory)) throw new InvalidOperationException("Seleccione un historial válido.");
            var result = await _expenseService.GetPageAsync(requestedPage, 100, start, end, activeShiftOnly: byShift, cancellationsOnly: cancellations);
            if (version != _loadVersion) return false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion) return;
                foreach (var expense in result.Items) Expenses.Add(expense);
                foreach (var reversal in result.Cancellations) Cancellations.Add(reversal);
                CancellationAmount = result.CancellationAmount;
                TotalCount = result.TotalCount; HasMore = result.HasMore;
                HasLoadedData = true;
                FinancialVisible = result.Summary is not null;
                var summary = result.Summary;
                TotalRevenue = Ingresos = summary?.TotalRevenue ?? 0m;
                TotalCostOfGoods = CostoDeVentas = Reinversion = summary?.TotalCostOfGoodsSold ?? 0m;
                GrossProfit = summary?.GrossProfit ?? 0m;
                TotalExpenses = Egresos = summary?.TotalExpenses ?? 0m;
                NetProfit = UtilidadNeta = summary?.NetProfit ?? 0m;
                PagosConTarjeta = (summary?.CardRevenue ?? 0m) + (summary?.TransferRevenue ?? 0m);
                _cashVisible = result.CashVisible; _hasActiveShift = result.ActiveShift is not null;
                FondoInicial = byShift ? result.ActiveShift?.OpeningBalance ?? 0m : 0m;
                TotalEnCaja = result.ActiveShift?.ExpectedBalance ?? 0m;
                PeriodSubtitleDisplay = byShift
                    ? result.ActiveShift is null ? "No hay turno activo en esta caja. Seleccione otro período."
                    : BusinessTime.TryLocal(result.ActiveShift.StartTime, out var opened)
                        ? $"Turno activo de esta caja · desde {opened:dd/MM/yyyy HH:mm}"
                        : "Turno activo de esta caja · fecha de apertura sin verificar"
                    : selected == "Todo el Histórico" ? "Histórico total de la tienda" : $"{selected} · desde {start:dd/MM/yyyy}";
                ReadMessage = cancellations
                    ? "Cancelaciones por fecha y turno receptor, con su gasto original aunque pertenezca a otro período. El resumen financiero sigue mostrando el período completo: gastos menos reversos, no sólo esta lista. Las cancelaciones heredadas sin diario sólo se muestran sin filtro de turno."
                    : "El historial muestra los gastos por fecha y turno de origen. Los totales abarcan todo el período e incluyen las cancelaciones recibidas, aunque el gasto original quede fuera del filtro. Consulte «Cancelaciones recibidas» para explicar los reversos. Cada consulta incorpora operaciones posteriores.";
                if (!FinancialVisible) ReadMessage += " Resumen financiero restringido por permisos.";
                if (result.UnparseableDateCount > 0)
                    ReadMessage += $" Hay {result.UnparseableDateCount} {(cancellations ? "cancelaciones" : "gastos")} con fecha sin verificar: no se incorporan a los totales. Consúltelos sin filtro de fechas.";
                OnPropertyChanged(nameof(OpeningBalanceDisplay)); OnPropertyChanged(nameof(CashBalanceDisplay));
                OnPropertyChanged(nameof(PageStatus)); OnPropertyChanged(nameof(CanLoadPreviousPage));
            });
            return version == _loadVersion;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading expense workspace");
            if (version == _loadVersion)
            {
                // Do not discard the retry identity of a monetary command with an uncertain response.
                ClearReadData();
                ReadMessage = "No se pudo actualizar la consulta. Datos no disponibles; verifique permisos y vuelva a consultar.";
            }
            return false;
        }
        finally { if (version == _loadVersion) IsLoading = false; }
    }
}
