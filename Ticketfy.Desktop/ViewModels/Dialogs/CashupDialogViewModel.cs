using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Services;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Serilog;
using Ticketfy.Core.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Ticketfy.ViewModels.Dialogs;

public record ForceLogoutMessage();

public partial class CashupDialogViewModel : ObservableObject
{
    private readonly AppDbContext _db;
    private readonly Ticketfy.Services.Interfaces.IShiftService? _shiftService;
    private readonly ISessionManager? _sessionManager;
    private readonly Ticketfy.Services.Interfaces.IEscPosPrinterService? _printerService;
    private readonly Ticketfy.Services.Interfaces.IBackupService? _backupService;
    private readonly Ticketfy.Services.Interfaces.IAttendanceService? _attendanceService;

    [ObservableProperty] private decimal _openCashAmount;
    [ObservableProperty] private decimal _totalIngresosShift;
    [ObservableProperty] private decimal _totalEgresosShift;
    [ObservableProperty] private decimal _theoreticalCash;
    [ObservableProperty] private decimal _totalPhysicalCash;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DifferenceAmountColor))]
    [NotifyPropertyChangedFor(nameof(DifferenceStatusLabel))]
    private decimal _differenceAmount;

    public string DifferenceAmountColor => Math.Abs(DifferenceAmount) < 0.001m ? "#10B981" : (DifferenceAmount > 0 ? "#3B82F6" : "#EF4444");
    public string DifferenceStatusLabel => Math.Abs(DifferenceAmount) < 0.001m ? "Cuadre Exacto" : (DifferenceAmount > 0 ? "Sobrante" : "Faltante");

    [ObservableProperty] private decimal _grossProfit;
    [ObservableProperty] private decimal _netProfit;
    [ObservableProperty] private bool _isBlindMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(IsPartialMode))]
    private bool _isFinalZCut = false;

    public bool IsPartialMode
    {
        get => !IsFinalZCut;
        set => IsFinalZCut = !value;
    }

    public string SaveButtonText => IsFinalZCut
        ? "GUARDAR CORTE FINAL Y CERRAR TURNO"
        : "GUARDAR CORTE PARCIAL";

    [ObservableProperty] private bool _isFeedbackError = false;
    private Ticketfy.Data.Dtos.ShiftDto? _activeShift;

    // Denomination Counts
    [ObservableProperty] private string _count1000 = "0";
    [ObservableProperty] private string _count500 = "0";
    [ObservableProperty] private string _count200 = "0";
    [ObservableProperty] private string _count100 = "0";
    [ObservableProperty] private string _count50 = "0";
    [ObservableProperty] private string _count20 = "0";
    [ObservableProperty] private string _count10 = "0";
    [ObservableProperty] private string _count5 = "0";
    [ObservableProperty] private string _count2 = "0";
    [ObservableProperty] private string _count1 = "0";
    [ObservableProperty] private string _count050 = "0";

    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public event Action? RequestClose;

    public CashupDialogViewModel(
        AppDbContext db,
        Ticketfy.Services.Interfaces.IShiftService? shiftService = null,
        ISessionManager? sessionManager = null,
        Ticketfy.Services.Interfaces.IEscPosPrinterService? printerService = null,
        Ticketfy.Services.Interfaces.IBackupService? backupService = null,
        bool isFinalZCut = false,
        bool isBlindMode = false,
        Ticketfy.Services.Interfaces.IAttendanceService? attendanceService = null)
    {
        _db = db;
        _shiftService = shiftService;
        _sessionManager = sessionManager;
        _printerService = printerService;
        _backupService = backupService;
        _attendanceService = attendanceService;
        IsFinalZCut = isFinalZCut;
        IsBlindMode = isBlindMode;

        _ = LoadActiveShiftDetailsAsync();
        RecalculatePhysicalTotal();
    }

    partial void OnCount1000Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount500Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount200Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount100Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount50Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount20Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount10Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount5Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount2Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount1Changed(string value) => RecalculatePhysicalTotal();
    partial void OnCount050Changed(string value) => RecalculatePhysicalTotal();

    private void RecalculatePhysicalTotal()
    {
        decimal.TryParse(Count1000, out decimal c1000);
        decimal.TryParse(Count500, out decimal c500);
        decimal.TryParse(Count200, out decimal c200);
        decimal.TryParse(Count100, out decimal c100);
        decimal.TryParse(Count50, out decimal c50);
        decimal.TryParse(Count20, out decimal c20);
        decimal.TryParse(Count10, out decimal c10);
        decimal.TryParse(Count5, out decimal c5);
        decimal.TryParse(Count2, out decimal c2);
        decimal.TryParse(Count1, out decimal c1);
        decimal.TryParse(Count050, out decimal c050);

        TotalPhysicalCash =
            (c1000 * 1000m) +
            (c500 * 500m) +
            (c200 * 200m) +
            (c100 * 100m) +
            (c50 * 50m) +
            (c20 * 20m) +
            (c10 * 10m) +
            (c5 * 5m) +
            (c2 * 2m) +
            c1 +
            (c050 * 0.50m);

        DifferenceAmount = TotalPhysicalCash - TheoreticalCash;
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private async Task OnViewLoadedAsync()
    {
        if (_printerService != null)
        {
            await _printerService.OpenCashDrawerAsync();
        }
    }

    [RelayCommand]
    private async Task SaveCashupAsync()
    {
        bool dbSavedSuccessfully = false;
        try
        {
            var entity = new CashupEntity
            {
                Id = Guid.NewGuid().ToString(),
                OpenCashAmount = OpenCashAmount,
                ClosedCashAmount = TotalPhysicalCash,
                Count1000 = int.TryParse(Count1000, out int i1000) ? i1000 : 0,
                Count500 = int.TryParse(Count500, out int i500) ? i500 : 0,
                Count200 = int.TryParse(Count200, out int i200) ? i200 : 0,
                Count100 = int.TryParse(Count100, out int i100) ? i100 : 0,
                Count50 = int.TryParse(Count50, out int i50) ? i50 : 0,
                Count20 = int.TryParse(Count20, out int i20) ? i20 : 0,
                Count10 = int.TryParse(Count10, out int i10) ? i10 : 0,
                Count5 = int.TryParse(Count5, out int i5) ? i5 : 0,
                Count2 = int.TryParse(Count2, out int i2) ? i2 : 0,
                Count1 = int.TryParse(Count1, out int i1) ? i1 : 0,
                Count050 = int.TryParse(Count050, out int i050) ? i050 : 0,
                TheoreticalCash = TheoreticalCash,
                Difference = DifferenceAmount,
                Notes = Notes,
                Type = IsFinalZCut ? "Final" : "Parcial",
                CashierName = _sessionManager?.CurrentCashier?.FullName ?? "Cajero en turno",
                CashierRole = _sessionManager?.CurrentCashier?.DisplayRole ?? "CAJERO",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            _db.Cashups.Add(entity);
            await _db.SaveChangesAsync();
            dbSavedSuccessfully = true;

            WeakReferenceMessenger.Default.Send(new Ticketfy.Core.Messages.CashupSavedMessage());

            IsFeedbackError = false;
            FeedbackMessage = IsFinalZCut
                ? "¡Cierre Z de Turno guardado correctamente!"
                : "¡Arqueo Parcial de Caja guardado correctamente!";

            // Print Thermal Audit Slip
            if (_printerService != null)
            {
                try
                {
                    await _printerService.PrintNonSaleCashMovementSlipAsync(new Ticketfy.Core.Models.ShiftMovementSlipModel
                    {
                        Folio = entity.Id.Length >= 8 ? entity.Id.Substring(0, 8).ToUpper() : entity.Id.ToUpper(),
                        MovementTypeLabel = IsFinalZCut ? "CORTE FINAL Z" : "ARQUEO PARCIAL DE CAJA",
                        Amount = entity.ClosedCashAmount,
                        Description = $"Físico: ${entity.ClosedCashAmount:N2} | Teórico: ${entity.TheoreticalCash:N2} | Dif: ${entity.Difference:N2}",
                        CashierName = _sessionManager?.CurrentCashier?.FullName ?? "CAJERO EN TURNO",
                        Timestamp = DateTime.Now
                    });
                }
                catch (Exception exPrint)
                {
                    Log.Warning(exPrint, "Aviso imprimiendo ticket de arqueo de caja");
                }
            }

            if (IsFinalZCut)
            {
                var activeShift = _activeShift ?? (_shiftService != null ? await _shiftService.GetActiveAsync() : null);
                string? shiftId = activeShift?.Id;

                if (string.IsNullOrEmpty(shiftId))
                {
                    var openEntity = await _db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.IsOpen == 1);
                    shiftId = openEntity?.Id;
                }

                if (_shiftService != null)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(shiftId))
                        {
                            await _shiftService.CloseAsync(shiftId, TotalPhysicalCash);
                            Log.Information("ShiftService: Closed shift {ShiftId} with physical cash ${Amount:N2}", shiftId, TotalPhysicalCash);
                        }
                        else
                        {
                            var newClosedShift = await _shiftService.OpenAsync(OpenCashAmount);
                            await _shiftService.CloseAsync(newClosedShift.Id, TotalPhysicalCash);
                            shiftId = newClosedShift.Id;
                            Log.Information("ShiftService: Created and closed placeholder shift {ShiftId}", newClosedShift.Id);
                        }
                    }
                    catch (Exception exShift)
                    {
                        Log.Warning(exShift, "Aviso cerrando el turno en ShiftService");
                    }
                }

                try
                {
                    if (_backupService != null)
                    {
                        string refId = (!string.IsNullOrEmpty(shiftId) && shiftId.Length >= 8) ? shiftId.Substring(0, 8) : (shiftId ?? "ZCUT");
                        await _backupService.CreateZCutBackupAsync(refId);
                    }
                }
                catch (Exception exBackup)
                {
                    Log.Warning(exBackup, "Aviso generando respaldo ZCutBackup");
                }

                try
                {
                    if (_sessionManager?.CurrentCashier != null && _attendanceService != null)
                    {
                        await _attendanceService.ClockOutAsync(_sessionManager.CurrentCashier.Id.ToString());
                    }
                }
                catch (Exception exClock)
                {
                    Log.Warning(exClock, "Aviso registrando salida de cajero");
                }

                try
                {
                    _sessionManager?.ClearSession();
                }
                catch (Exception exSession)
                {
                    Log.Warning(exSession, "Aviso limpiando sesión");
                }

                // Close the modal dialog first, then post ForceLogoutMessage to seamlessly switch shell to login screen!
                RequestClose?.Invoke();

                Dispatcher.UIThread.Post(() =>
                {
                    WeakReferenceMessenger.Default.Send(new ForceLogoutMessage());
                });

                return;
            }

            await Task.Delay(400);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving cashup audit entity");
            if (dbSavedSuccessfully)
            {
                // Entity was saved to DB successfully, close dialog cleanly!
                RequestClose?.Invoke();
            }
            else
            {
                IsFeedbackError = true;
                FeedbackMessage = "Error al guardar el registro del arqueo en la base de datos.";
            }
        }
    }

    private async Task LoadActiveShiftDetailsAsync()
    {
        try
        {
            var shift = _shiftService != null ? await _shiftService.GetActiveAsync() : null;
            DateTime? shiftStart = null;
            decimal openingBalance = 0m;
            string? shiftId = null;

            if (shift != null)
            {
                _activeShift = shift;
                openingBalance = shift.OpeningBalance;
                shiftId = shift.Id;
                if (DateTime.TryParse(shift.StartTime, out var parsedStart))
                {
                    shiftStart = parsedStart;
                }
            }
            else
            {
                // Fallback to start of current calendar day if no active shift entity found
                shiftStart = DateTime.Today;
            }

            // Drawer reconciliation includes the original inflow even when a sale
            // was later cancelled/refunded; the matching cash return movement is
            // subtracted below. Excluding that sale would count the refund twice.
            var query = _db.Sales.AsNoTracking();

            if (shiftStart.HasValue)
            {
                string startIso = shiftStart.Value.ToString("yyyy-MM-dd");
                query = query.Where(s => string.Compare(s.Date, startIso) >= 0);
            }

            var allSales = await query.ToListAsync();

            var shiftSales = allSales
                .Where(s => s.Date.IsInDateRange(shiftStart, null))
                .ToList();

            var cashSales = shiftSales
                .Sum(s => s.CashAmount > 0 ? s.CashAmount :
                    (s.PaymentMethod != null && (s.PaymentMethod.Equals("Cash", StringComparison.OrdinalIgnoreCase) || s.PaymentMethod.Equals("Efectivo", StringComparison.OrdinalIgnoreCase)) ? s.Total : 0m));

            decimal customerAbonosCash = 0m;
            decimal cashExpenses = 0m;
            decimal cashPurchases = 0m;
            decimal cashReturns = 0m;
            decimal cashWithdrawals = 0m;

            if (!string.IsNullOrEmpty(shiftId))
            {
                customerAbonosCash = await _db.ShiftMovements
                    .AsNoTracking()
                    .Where(m => m.ShiftId == shiftId && m.MovementType == Ticketfy.Core.Enums.MovementType.AbonoCliente)
                    .SumAsync(m => m.Amount);

                cashExpenses = await _db.ShiftMovements
                    .AsNoTracking()
                    .Where(m => m.ShiftId == shiftId && m.MovementType == Ticketfy.Core.Enums.MovementType.GastoOperativo)
                    .SumAsync(m => m.Amount);

                cashPurchases = await _db.ShiftMovements
                    .AsNoTracking()
                    .Where(m => m.ShiftId == shiftId && m.MovementType == Ticketfy.Core.Enums.MovementType.CompraEfectivo)
                    .SumAsync(m => m.Amount);

                cashReturns = await _db.ShiftMovements
                    .AsNoTracking()
                    .Where(m => m.ShiftId == shiftId && m.MovementType == Ticketfy.Core.Enums.MovementType.DevolucionCliente)
                    .SumAsync(m => m.Amount);

                cashWithdrawals = await _db.ShiftMovements
                    .AsNoTracking()
                    .Where(m => m.ShiftId == shiftId && m.MovementType == Ticketfy.Core.Enums.MovementType.RetiroEfectivo)
                    .SumAsync(m => m.Amount);
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                OpenCashAmount = openingBalance;
                TotalIngresosShift = cashSales + customerAbonosCash;
                TotalEgresosShift = cashExpenses + cashPurchases + cashReturns + cashWithdrawals;
                TheoreticalCash = OpenCashAmount + TotalIngresosShift - TotalEgresosShift;

                decimal allSalesTotal = shiftSales.Sum(s => s.Total);
                decimal allSalesCogs = shiftSales.Sum(s => s.TotalCost);
                decimal allExpensesTotal = cashExpenses;

                GrossProfit = Math.Max(0m, allSalesTotal - allSalesCogs);
                NetProfit = GrossProfit - allExpensesTotal;

                RecalculatePhysicalTotal();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading active shift details for cashup dialog");
        }
    }
}
