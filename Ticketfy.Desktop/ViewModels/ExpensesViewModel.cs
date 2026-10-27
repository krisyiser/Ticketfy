using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class ExpensesViewModel : ObservableObject
{
    private string _expenseOperationId = Guid.NewGuid().ToString("N");
    private bool _savingExpense;
    private int _loadVersion;
    private long _sessionVersion;
    public void ClearSessionData()
    {
        ++_sessionVersion; ++_loadVersion;
        _page = 0; IsLoading = false;
        ClearReadData();
        ExpenseAmount = 0m;
        ExpenseDescription = ExpenseReference = CancellationReason = FeedbackMessage = string.Empty;
        ReversalVerified = false;
        _expenseOperationId = Guid.NewGuid().ToString("N");
    }
    private void ClearReadData()
    {
        Expenses.Clear(); Cancellations.Clear(); CancellationAmount = 0m;
        TotalRevenue = TotalCostOfGoods = GrossProfit = TotalExpenses = NetProfit = 0m;
        FondoInicial = Ingresos = PagosConTarjeta = CostoDeVentas = Egresos = TotalEnCaja = Reinversion = UtilidadNeta = 0m;
        PeriodSubtitleDisplay = string.Empty;
        HasLoadedData = FinancialVisible = HasMore = false;
        TotalCount = 0;
        ReadMessage = "Sin consulta disponible.";
        OnPropertyChanged(nameof(PageStatus));
    }
    [ObservableProperty] private string _expenseReference = string.Empty;
    [ObservableProperty] private string _cancellationReason = string.Empty;
    [ObservableProperty] private bool _reversalVerified;
    private readonly IExpenseService _expenseService;

    public ObservableCollection<ExpenseDto> Expenses { get; } = [];
    public ObservableCollection<ExpenseCancellationDto> Cancellations { get; } = [];
    public ObservableCollection<string> HistoryOptions { get; } = ["Documentos de gasto", "Cancelaciones recibidas"];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingCancellations), nameof(PageStatus))]
    private string _selectedHistory = "Documentos de gasto";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancellationAmountDisplay))]
    private decimal _cancellationAmount;
    public string CancellationAmountDisplay => HasLoadedData
        ? $"Reversos con fecha verificable de todo el período: {CancellationAmount:C2}" : "Reversos del período: no disponibles";
    public bool ShowingCancellations => SelectedHistory == "Cancelaciones recibidas";
    partial void OnSelectedHistoryChanged(string value) => _ = LoadExpensesAsync();
    public ObservableCollection<string> Categories { get; } = [
        "Renta", "Servicios (Luz/Agua/Internet)", "Nómina / Salarios", "Mantenimiento", "Transporte", "Varios"
    ];
    public ObservableCollection<string> PaymentMethods { get; } = [
        "Efectivo", "Tarjeta", "Transferencia"
    ];

    public ObservableCollection<string> PeriodOptions { get; } = [
        "Turno Activo", "Hoy", "Esta Semana", "Este Mes", "Todo el Histórico"
    ];

    [ObservableProperty] private string _selectedPeriod = "Turno Activo";
    [ObservableProperty] private string _periodSubtitleDisplay = string.Empty;

    public event Action? OpenCashupRequested;

    [RelayCommand]
    private void PerformCashCutoff() => OpenCashupRequested?.Invoke();

    partial void OnSelectedPeriodChanged(string value)
    {
        _ = LoadExpensesAsync();
    }

    [ObservableProperty] private string _selectedCategory = "Renta";
    [ObservableProperty] private decimal _expenseAmount;
    [ObservableProperty] private string _expenseDescription = string.Empty;
    [ObservableProperty] private string _paymentMethod = "Efectivo";

    [ObservableProperty] private decimal _totalRevenue;
    [ObservableProperty] private decimal _totalCostOfGoods;
    [ObservableProperty] private decimal _grossProfit;
    [ObservableProperty] private decimal _totalExpenses;
    [ObservableProperty] private decimal _netProfit;

    // Cash-Flow Metrics (Decimal representation)
    [ObservableProperty] private decimal _fondoInicial;
    [ObservableProperty] private decimal _ingresos;
    [ObservableProperty] private decimal _pagosConTarjeta;
    [ObservableProperty] private decimal _costoDeVentas;
    [ObservableProperty] private decimal _egresos;
    [ObservableProperty] private decimal _totalEnCaja;
    [ObservableProperty] private decimal _reinversion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UtilidadNetaColor))]
    private decimal _utilidadNeta;

    public string UtilidadNetaColor => UtilidadNeta >= 0 ? "#059669" : "#EF4444";

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;

    public Task InitializationTask { get; }
    public ExpensesViewModel(IExpenseService expenseService, bool loadOnCreate = true)
    {
        _expenseService = expenseService;
        InitializationTask = loadOnCreate ? LoadExpensesAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddExpenseAsync()
    {
        if (_savingExpense) return;
        if (ExpenseAmount <= 0)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Ingrese un monto de gasto válido mayor a cero";
            return;
        }

        var version = _sessionVersion;
        try
        {
            _savingExpense = true;
            var dto = new ExpenseDto(
                _expenseOperationId,
                SelectedCategory,
                ExpenseAmount,
                DateTimeOffset.UtcNow.ToString("O"),
                ExpenseDescription,
                PaymentMethod,
                string.Empty, OperationId: _expenseOperationId, Reference: ExpenseReference
            );

            await _expenseService.CreateAsync(dto);
            if (version != _sessionVersion) return;
            _expenseOperationId = Guid.NewGuid().ToString("N");

            // COMPLETE FORM RESET UPON SAVE
            ExpenseAmount = 0;
            ExpenseDescription = ExpenseReference = string.Empty;
            IsFeedbackError = false;
            FeedbackMessage = "¡Gasto operativo registrado correctamente!";

            if (!await LoadPageAsync(0) && version == _sessionVersion)
            {
                IsFeedbackError = false;
                FeedbackMessage = "Gasto registrado. No se pudo actualizar la consulta; vuelva a consultar sin repetir el pago.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding expense");
            if (version != _sessionVersion) return;
            IsFeedbackError = true;
            FeedbackMessage = ex.Message;
        }
        finally { _savingExpense = false; }
    }

    [RelayCommand]
    private async Task DeleteExpenseAsync(ExpenseDto expense)
    {
        if (_savingExpense || expense.IsCancelled || !HasLoadedData || ShowingCancellations
            || !Expenses.Any(row => ReferenceEquals(row, expense))) return;
        var version = _sessionVersion;
        try
        {
            _savingExpense = true;
            bool cancelled = await _expenseService.CancelAsync(expense.Id, CancellationReason, $"VOID:{expense.Id}", ReversalVerified);
            if (version != _sessionVersion) return;
            if (!cancelled) throw new InvalidOperationException("El gasto ya no está disponible. Actualice la consulta.");
            CancellationReason = ""; ReversalVerified = false;
            IsFeedbackError = false; FeedbackMessage = "Gasto cancelado; se conserva el original y su compensación.";
            if (!await LoadPageAsync(0) && version == _sessionVersion)
            {
                IsFeedbackError = false;
                FeedbackMessage = "Gasto cancelado. No se pudo actualizar la consulta; no repita la restitución.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting expense");
            if (version != _sessionVersion) return;
            IsFeedbackError = true; FeedbackMessage = ex.Message;
        }
        finally { _savingExpense = false; }
    }
}
