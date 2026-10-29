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
    [ObservableProperty] private bool _isSavingEntry;
    private int _loadVersion;
    private long _sessionVersion;
    public void ClearSessionData()
    {
        ++_sessionVersion; ++_loadVersion;
        _page = 0; IsLoading = false;
        ClearReadData();
        ExpenseAmount = 0m;
        EntryKind = "Gasto operativo";
        CashMovements.Clear();
        _cashOperationId = Guid.NewGuid().ToString("N");
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
    private readonly IShiftService? _shiftService;
    private string _cashOperationId = Guid.NewGuid().ToString("N");
    public ObservableCollection<CashMovementRow> CashMovements { get; } = [];
    public ObservableCollection<string> EntryKinds { get; } = ["Gasto operativo"];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpenseEntry), nameof(RegisterButtonText))]
    private string _entryKind = "Gasto operativo";
    public bool IsExpenseEntry => EntryKind == "Gasto operativo";
    public string RegisterButtonText => IsExpenseEntry ? "REGISTRAR GASTO"
        : EntryKind == "Ingreso de efectivo" ? "REGISTRAR INGRESO" : "REGISTRAR RETIRO";

    public ObservableCollection<ExpenseDto> Expenses { get; } = [];
    public ObservableCollection<ExpenseCancellationDto> Cancellations { get; } = [];
    public ObservableCollection<string> HistoryOptions { get; } = ["Documentos de gasto", "Cancelaciones recibidas"];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingCancellations), nameof(PageStatus), nameof(ShowingCashMovements), nameof(ShowingExpenses))]
    private string _selectedHistory = "Documentos de gasto";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancellationAmountDisplay))]
    private decimal _cancellationAmount;
    public string CancellationAmountDisplay => HasLoadedData
        ? $"Reversos con fecha verificable de todo el período: {CancellationAmount:C2}" : "Reversos del período: no disponibles";
    public bool ShowingCancellations => SelectedHistory == "Cancelaciones recibidas";
    public bool ShowingCashMovements => SelectedHistory == "Ingresos y retiros del turno";
    public bool ShowingExpenses => !ShowingCancellations && !ShowingCashMovements;
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
    public ExpensesViewModel(IExpenseService expenseService, bool loadOnCreate = true, IShiftService? shiftService = null)
    {
        _expenseService = expenseService;
        _shiftService = shiftService;
        if (shiftService is not null)
        {
            EntryKinds.Add("Ingreso de efectivo"); EntryKinds.Add("Retiro de efectivo");
            HistoryOptions.Add("Ingresos y retiros del turno");
        }
        InitializationTask = loadOnCreate ? LoadExpensesAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddExpenseAsync()
    {
        if (IsSavingEntry) return;
        if (!IsExpenseEntry) { await AddCashMovementAsync(); return; }
        if (ExpenseAmount <= 0)
        {
            IsFeedbackError = true;
            FeedbackMessage = "Ingrese un monto de gasto válido mayor a cero";
            return;
        }

        var version = _sessionVersion;
        try
        {
            IsSavingEntry = true;
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
            FeedbackMessage = Ticketfy.Core.Helpers.UserFacingErrors.Message(ex);
        }
        finally { IsSavingEntry = false; }
    }

    private async Task AddCashMovementAsync()
    {
        if (_shiftService is null || !EntryKinds.Contains(EntryKind)) return;
        if (ExpenseAmount <= 0 || decimal.Round(ExpenseAmount, 2) != ExpenseAmount || string.IsNullOrWhiteSpace(ExpenseDescription))
        {
            IsFeedbackError = true;
            FeedbackMessage = "Indique un importe positivo con hasta dos decimales y el motivo del movimiento.";
            return;
        }
        var version = _sessionVersion;
        try
        {
            IsSavingEntry = true;
            await _shiftService.RegisterMovementAsync(ExpenseAmount, EntryKind == "Retiro de efectivo", ExpenseDescription.Trim(), _cashOperationId);
            if (version != _sessionVersion) return;
            _cashOperationId = Guid.NewGuid().ToString("N");
            ExpenseAmount = 0m; ExpenseDescription = string.Empty;
            IsFeedbackError = false;
            FeedbackMessage = "Movimiento registrado en el turno y en el historial de caja.";
            if (!await LoadPageAsync(0) && version == _sessionVersion)
                FeedbackMessage = "Movimiento registrado. Actualice la consulta sin repetir la entrada o salida de efectivo.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error recording manual cash movement");
            if (version != _sessionVersion) return;
            IsFeedbackError = true;
            FeedbackMessage = Ticketfy.Core.Helpers.UserFacingErrors.Message(ex);
        }
        finally { IsSavingEntry = false; }
    }

    [RelayCommand]
    private async Task DeleteExpenseAsync(ExpenseDto expense)
    {
        if (IsSavingEntry || expense.IsCancelled || !HasLoadedData || ShowingCancellations
            || !Expenses.Any(row => ReferenceEquals(row, expense))) return;
        var version = _sessionVersion;
        try
        {
            IsSavingEntry = true;
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
            IsFeedbackError = true; FeedbackMessage = Ticketfy.Core.Helpers.UserFacingErrors.Message(ex);
        }
        finally { IsSavingEntry = false; }
    }
}
