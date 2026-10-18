using Avalonia.Threading;
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
    [ObservableProperty] private string _expenseReference = string.Empty;
    [ObservableProperty] private string _cancellationReason = string.Empty;
    [ObservableProperty] private bool _reversalVerified;
    private readonly IExpenseService _expenseService;
    private readonly IShiftService _shiftService;

    public ObservableCollection<ExpenseDto> Expenses { get; } = [];
    public ObservableCollection<string> Categories { get; } = [
        "Renta", "Servicios (Luz/Agua/Internet)", "Nómina / Salarios", "Mantenimiento", "Transporte", "Varios"
    ];
    public ObservableCollection<string> PaymentMethods { get; } = [
        "Efectivo", "Tarjeta / Transferencia"
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

    public ExpensesViewModel(IExpenseService expenseService, IShiftService shiftService)
    {
        _expenseService = expenseService;
        _shiftService = shiftService;
        _ = LoadExpensesAsync();
    }

    public async Task LoadExpensesAsync()
    {
        try
        {
            var list = await _expenseService.GetAllAsync();
            var activeShift = await _shiftService.GetActiveAsync();

            DateTime? shiftStart = null;
            DateTime? shiftEnd = DateTime.Now;

            if (SelectedPeriod == "Turno Activo")
            {
                if (activeShift != null && !string.IsNullOrWhiteSpace(activeShift.StartTime))
                {
                    if (DateTime.TryParse(activeShift.StartTime, out var parsedStart))
                    {
                        shiftStart = parsedStart;
                        PeriodSubtitleDisplay = $"Turno Activo (Desde: {parsedStart:dd/MM/yyyy hh:mm tt})";
                    }
                }
                if (!shiftStart.HasValue)
                {
                    shiftStart = DateTime.Today;
                    PeriodSubtitleDisplay = $"Turno Activo (Desde Hoy: {DateTime.Today:dd/MM/yyyy})";
                }
            }
            else if (SelectedPeriod == "Hoy")
            {
                shiftStart = DateTime.Today;
                PeriodSubtitleDisplay = $"Acumulado de Hoy ({DateTime.Today:dd/MM/yyyy})";
            }
            else if (SelectedPeriod == "Esta Semana")
            {
                int diff = (7 + (DateTime.Today.DayOfWeek - DayOfWeek.Monday)) % 7;
                shiftStart = DateTime.Today.AddDays(-1 * diff);
                PeriodSubtitleDisplay = $"Acumulado de Esta Semana (Desde: {shiftStart.Value:dd/MM/yyyy})";
            }
            else if (SelectedPeriod == "Este Mes")
            {
                shiftStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                PeriodSubtitleDisplay = $"Acumulado de Este Mes ({shiftStart.Value:MMMM yyyy})";
            }
            else // Todo el Histórico
            {
                shiftStart = null;
                shiftEnd = null;
                PeriodSubtitleDisplay = "Acumulado Histórico Total de la Tienda";
            }

            var summary = await _expenseService.GetFinancialSummaryAsync(shiftStart, shiftEnd);
            decimal openingBalance = (SelectedPeriod == "Turno Activo" && activeShift != null) ? activeShift.OpeningBalance : 0m;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Expenses.Clear();
                foreach (var e in list) Expenses.Add(e);

                TotalRevenue = summary.TotalRevenue;
                TotalCostOfGoods = summary.TotalCostOfGoodsSold;
                GrossProfit = summary.GrossProfit;
                TotalExpenses = summary.TotalExpenses;
                NetProfit = summary.NetProfit;

                FondoInicial = openingBalance;
                Ingresos = summary.TotalRevenue;
                PagosConTarjeta = summary.CardRevenue;
                CostoDeVentas = summary.TotalCostOfGoodsSold;
                Egresos = summary.TotalExpenses;

                // Total en Caja (Fondo Inicial + Ventas en Efectivo - Gastos en Efectivo)
                TotalEnCaja = activeShift?.ExpectedBalance ?? 0m;

                Reinversion = CostoDeVentas;
                // Utilidad Neta Real = Ventas - Costo de Ventas (COGS) - Gastos Operativos
                UtilidadNeta = Ingresos - CostoDeVentas - Egresos;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading expenses data");
        }
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

        try
        {
            _savingExpense = true;
            var dto = new ExpenseDto(
                _expenseOperationId,
                SelectedCategory,
                ExpenseAmount,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ExpenseDescription,
                PaymentMethod,
                string.Empty, OperationId: _expenseOperationId, Reference: ExpenseReference
            );

            var created = await _expenseService.CreateAsync(dto);
            Expenses.Insert(0, created);
            _expenseOperationId = Guid.NewGuid().ToString("N");

            // COMPLETE FORM RESET UPON SAVE
            ExpenseAmount = 0;
            ExpenseDescription = string.Empty;
            IsFeedbackError = false;
            FeedbackMessage = "¡Gasto operativo registrado correctamente!";

            await LoadExpensesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding expense");
            IsFeedbackError = true;
            FeedbackMessage = ex.Message;
        }
        finally { _savingExpense = false; }
    }

    [RelayCommand]
    private async Task DeleteExpenseAsync(ExpenseDto expense)
    {
        if (_savingExpense || expense.IsCancelled) return;
        try
        {
            _savingExpense = true;
            await _expenseService.CancelAsync(expense.Id, CancellationReason, $"VOID:{expense.Id}", ReversalVerified);
            CancellationReason = ""; ReversalVerified = false;
            IsFeedbackError = false; FeedbackMessage = "Gasto cancelado; se conserva el original y su compensación.";
            await LoadExpensesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting expense");
            IsFeedbackError = true; FeedbackMessage = ex.Message;
        }
        finally { _savingExpense = false; }
    }
}
