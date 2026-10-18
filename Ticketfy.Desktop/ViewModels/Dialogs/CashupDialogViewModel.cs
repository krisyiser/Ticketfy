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

    private readonly string _cashupOperationId = Guid.NewGuid().ToString("N");
    private bool _savingCashup;
    private bool _cashupCommitted;

    private bool CanSaveCashup() => !_savingCashup && !_cashupCommitted;

    [RelayCommand]
    private async Task SaveCashupAsync()
    {
        if (!CanSaveCashup()) return;
        _savingCashup = true;
        bool dbSavedSuccessfully = false;
        try
        {
            var entity = new CashupEntity
            {
                Id = _cashupOperationId,
                OperationId = _cashupOperationId,
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

            var activeShift = _activeShift ?? (_shiftService != null ? await _shiftService.GetActiveAsync() : null);
            if (activeShift is null || _shiftService is null) throw new InvalidOperationException("No hay turno activo para registrar el arqueo.");
            await _shiftService.RecordCountAsync(activeShift.Id, entity);
            _cashupCommitted = true;
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
                    bool sent = await _printerService.PrintNonSaleCashMovementSlipAsync(new Ticketfy.Core.Models.ShiftMovementSlipModel
                    {
                        Folio = entity.Id.Length >= 8 ? entity.Id.Substring(0, 8).ToUpper() : entity.Id.ToUpper(),
                        MovementTypeLabel = IsFinalZCut ? "CORTE FINAL Z" : "ARQUEO PARCIAL DE CAJA",
                        Amount = entity.ClosedCashAmount,
                        Description = $"Físico: ${entity.ClosedCashAmount:N2} | Teórico: ${entity.TheoreticalCash:N2} | Dif: ${entity.Difference:N2}",
                        CashierName = _sessionManager?.CurrentCashier?.FullName ?? "CAJERO EN TURNO",
                        Timestamp = DateTime.Now
                    });
                    if (!sent) FeedbackMessage += " No se confirmó la impresión; consulte el historial antes de reintentar.";
                }
                catch (Exception exPrint)
                {
                    Log.Warning(exPrint, "Aviso imprimiendo ticket de arqueo de caja");
                    Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("Arqueo registrado, pero falló la impresión. No repita el cierre.", true);
                }
            }

            if (IsFinalZCut)
            {
                string shiftId = activeShift.Id;

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
                FeedbackMessage = ex.Message;
            }
        }
        finally { _savingCashup = false; }
    }

    private async Task LoadActiveShiftDetailsAsync()
    {
        try
        {
            var shift = _shiftService != null ? await _shiftService.GetActiveAsync() : null;
            if (shift is null) { IsFeedbackError = true; FeedbackMessage = "No hay turno activo."; return; }
            _activeShift = shift;
            await using var db = new AppDbContext(_db.Options);
            var movements = await db.ShiftMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).ToListAsync();
            var sales = await db.Sales.AsNoTracking().Where(s => s.ShiftId == shift.Id).ToListAsync();
            var returns = await db.Returns.AsNoTracking().Where(r => r.ShiftId == shift.Id).ToListAsync();
            var expenses = await db.Expenses.AsNoTracking().Where(e => e.ShiftId == shift.Id && !e.IsCancelled).ToListAsync();
            OpenCashAmount = shift.OpeningBalance;
            TotalIngresosShift = movements.Where(m => !m.IsOutflow).Sum(m => m.Amount);
            TotalEgresosShift = movements.Where(m => m.IsOutflow).Sum(m => m.Amount);
            TheoreticalCash = OpenCashAmount + TotalIngresosShift - TotalEgresosShift;
            GrossProfit = sales.Sum(s => s.Total - s.TotalCost) - returns.Sum(r => r.TotalRefunded - r.CogsReversed);
            NetProfit = GrossProfit - expenses.Sum(e => e.Amount);
            RecalculatePhysicalTotal();
        }
        catch (Exception ex) { IsFeedbackError = true; FeedbackMessage = ex.Message; }
    }
}
