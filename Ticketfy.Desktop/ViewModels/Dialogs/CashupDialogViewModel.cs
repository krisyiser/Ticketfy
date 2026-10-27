using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Services;
using Ticketfy.Data.Entities;
using Serilog;
using Ticketfy.Core.Helpers;
using System;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.ViewModels.Dialogs;

public record ForceLogoutMessage(long? SessionVersion = null);

public partial class CashupDialogViewModel : ObservableObject
{
    private readonly IShiftService _shiftService;
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

    public string DifferenceAmountColor => !HasValidCounts ? "#EF4444" : Math.Abs(DifferenceAmount) < 0.001m ? "#10B981" : (DifferenceAmount > 0 ? "#3B82F6" : "#EF4444");
    public string DifferenceStatusLabel => !HasValidCounts ? "Conteo inválido" : Math.Abs(DifferenceAmount) < 0.001m ? "Cuadre Exacto" : (DifferenceAmount > 0 ? "Sobrante" : "Faltante");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DifferenceAmountColor))]
    [NotifyPropertyChangedFor(nameof(DifferenceStatusLabel))]
    [NotifyPropertyChangedFor(nameof(CountValidationMessage))]
    private bool _hasValidCounts = true;
    public string CountValidationMessage => HasValidCounts ? "" : "Indique cantidades enteras no negativas de billetes y monedas.";

    [ObservableProperty] private decimal _grossProfit;
    [ObservableProperty] private decimal _netProfit;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _isBlindMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(IsPartialMode))]
    private bool _isFinalZCut = false;

    public bool IsPartialMode
    {
        get => !IsFinalZCut;
        set => IsFinalZCut = !value;
    }

    public string SaveButtonText => IsFinalZCut || IsBlindMode
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
        IShiftService shiftService,
        ISessionManager? sessionManager = null,
        Ticketfy.Services.Interfaces.IEscPosPrinterService? printerService = null,
        Ticketfy.Services.Interfaces.IBackupService? backupService = null,
        bool isFinalZCut = false,
        bool isBlindMode = false,
        Ticketfy.Services.Interfaces.IAttendanceService? attendanceService = null)
    {
        ArgumentNullException.ThrowIfNull(shiftService);
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
        HasValidCounts = TryReadCounts(out var count);
        TotalPhysicalCash = count?.Total ?? 0m;
        DifferenceAmount = HasValidCounts && !IsBlindMode ? TotalPhysicalCash - TheoreticalCash : 0m;
    }
    private bool TryReadCounts(out CashDenominationCount? count) => CashDenominationCount.TryParse(
        [Count1000, Count500, Count200, Count100, Count50, Count20, Count10, Count5, Count2, Count1, Count050], out count);

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
    private bool IsCurrentSession(long? version) => _sessionManager is null
        || (_sessionManager.SessionVersion == version && !_sessionManager.IsTerminalLocked && _sessionManager.CurrentCashier is not null);

    private bool CanSaveCashup() => !_savingCashup && !_cashupCommitted;

    [RelayCommand]
    private async Task SaveCashupAsync()
    {
        if (!CanSaveCashup()) return;
        _savingCashup = true;
        long? version = _sessionManager?.SessionVersion;
        var cashier = _sessionManager?.CurrentCashier;
        bool finalCut = IsFinalZCut || IsBlindMode;
        bool dbSavedSuccessfully = false;
        try
        {
            if (!TryReadCounts(out var count) || count is null)
                throw new InvalidOperationException("Indique cantidades enteras no negativas de billetes y monedas.");
            var entity = new CashupEntity
            {
                Id = _cashupOperationId,
                OperationId = _cashupOperationId,
                OpenCashAmount = OpenCashAmount,
                ClosedCashAmount = count.Total,
                Count1000 = count.Thousand,
                Count500 = count.FiveHundred,
                Count200 = count.TwoHundred,
                Count100 = count.Hundred,
                Count50 = count.Fifty,
                Count20 = count.Twenty,
                Count10 = count.Ten,
                Count5 = count.Five,
                Count2 = count.Two,
                Count1 = count.One,
                Count050 = count.Half,
                TheoreticalCash = TheoreticalCash,
                Difference = DifferenceAmount,
                Notes = Notes,
                Type = finalCut ? "Final" : "Parcial",
                CashierName = cashier?.FullName ?? "Cajero en turno",
                CashierRole = cashier?.DisplayRole ?? "CAJERO",
                Timestamp = DateTimeOffset.UtcNow.ToString("O")
            };

            var activeShift = _activeShift ?? await _shiftService.GetActiveAsync();
            if (!IsCurrentSession(version)) return;
            if (activeShift is null) throw new InvalidOperationException("No hay turno activo para registrar el arqueo.");
            await _shiftService.RecordCountAsync(activeShift.Id, entity);
            _cashupCommitted = true;
            dbSavedSuccessfully = true;

            WeakReferenceMessenger.Default.Send(new Ticketfy.Core.Messages.CashupSavedMessage());

            IsFeedbackError = false;
            FeedbackMessage = finalCut
                ? "¡Cierre Z de Turno guardado correctamente!"
                : "¡Arqueo Parcial de Caja guardado correctamente!";

            // Print Thermal Audit Slip
            if (_printerService != null && IsCurrentSession(version))
            {
                try
                {
                    if (!BusinessTime.TryLocal(entity.Timestamp, out var recordedAt))
                        throw new InvalidOperationException("El corte no tiene una fecha interpretable para imprimir.");
                    bool sent = await _printerService.PrintNonSaleCashMovementSlipAsync(new Ticketfy.Core.Models.ShiftMovementSlipModel
                    {
                        Folio = entity.Id.Length >= 8 ? entity.Id.Substring(0, 8).ToUpper() : entity.Id.ToUpper(),
                        MovementTypeLabel = finalCut ? "CORTE FINAL Z" : "ARQUEO PARCIAL DE CAJA",
                        Amount = entity.ClosedCashAmount,
                        Description = $"Físico: ${entity.ClosedCashAmount:N2} | Teórico: ${entity.TheoreticalCash:N2} | Dif: ${entity.Difference:N2}",
                        CashierName = entity.CashierName,
                        Timestamp = recordedAt
                    });
                    if (!sent)
                    {
                        FeedbackMessage += " No se confirmó la impresión; consulte el historial antes de reintentar.";
                        Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("Arqueo registrado, pero no se confirmó la impresión. No repita el cierre.", true);
                    }
                }
                catch (Exception exPrint)
                {
                    Log.Warning(exPrint, "Aviso imprimiendo ticket de arqueo de caja");
                    Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("Arqueo registrado, pero falló la impresión. No repita el cierre.", true);
                }
            }

            if (finalCut)
            {
                string shiftId = activeShift.Id;

                try
                {
                    if (_backupService != null)
                    {
                        string refId = (!string.IsNullOrEmpty(shiftId) && shiftId.Length >= 8) ? shiftId.Substring(0, 8) : (shiftId ?? "ZCUT");
                        if (!await _backupService.CreateZCutBackupAsync(refId))
                            Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("Cierre guardado, pero no se creó el respaldo. Solicite una copia al administrador; no repita el cierre.", true);
                    }
                }
                catch (Exception exBackup)
                {
                    Log.Warning(exBackup, "Aviso generando respaldo ZCutBackup");
                    Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("Cierre guardado, pero falló el respaldo. Solicite una copia al administrador; no repita el cierre.", true);
                }

                try
                {
                    if (cashier != null && _attendanceService != null && IsCurrentSession(version))
                    {
                        var attendance = await _attendanceService.ClockOutAsync(cashier.Id.ToString());
                        if (!attendance.IsSuccess) Ticketfy.Core.Messages.HardwareFeedbackMessage.Report(
                            "Cierre guardado. La salida de asistencia requiere revisión del supervisor; no repita el cierre.", true);
                    }
                }
                catch (Exception exClock)
                {
                    Log.Warning(exClock, "Aviso registrando salida de cajero");
                    Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("Cierre guardado. No se confirmó la salida de asistencia; solicite revisión al supervisor.", true);
                }

                if (!IsCurrentSession(version)) return;
                RequestClose?.Invoke();
                try
                {
                    _sessionManager?.ClearSession();
                }
                catch (Exception exSession)
                {
                    Log.Warning(exSession, "Aviso limpiando sesión");
                }

                // A delayed close must never log out a newly authenticated operator.
                WeakReferenceMessenger.Default.Send(new ForceLogoutMessage(_sessionManager?.SessionVersion));

                return;
            }

            await Task.Delay(400);
            if (IsCurrentSession(version)) RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving cashup audit entity");
            if (dbSavedSuccessfully)
            {
                // Entity was saved to DB successfully, close dialog cleanly!
                if (IsCurrentSession(version)) RequestClose?.Invoke();
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
        long? version = _sessionManager?.SessionVersion;
        try
        {
            if (IsBlindMode)
            {
                var active = await _shiftService.GetActiveAsync();
                if (!IsCurrentSession(version)) return;
                _activeShift = active;
                if (active is null) { IsFeedbackError = true; FeedbackMessage = "No hay turno activo."; }
                // Keep only identity for saving. No expected amounts or movement totals are bound to the blind form.
                return;
            }
            var summary = await _shiftService.GetActiveCashSummaryAsync();
            if (!IsCurrentSession(version)) return;
            if (summary is null) { IsFeedbackError = true; FeedbackMessage = "No hay turno activo."; return; }
            var shift = summary.Shift;
            _activeShift = shift;
            OpenCashAmount = shift.OpeningBalance;
            TotalIngresosShift = summary.Inflows;
            TotalEgresosShift = summary.Outflows;
            TheoreticalCash = OpenCashAmount + TotalIngresosShift - TotalEgresosShift;
            RecalculatePhysicalTotal();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cash count preview unavailable");
            if (IsCurrentSession(version)) { IsFeedbackError = true; FeedbackMessage = "No se pudo consultar el turno. Verifique permisos, responsable y conciliación de caja."; }
        }
    }
}
