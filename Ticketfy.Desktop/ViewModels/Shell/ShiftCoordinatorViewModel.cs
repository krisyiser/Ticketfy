using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Services;
using Ticketfy.Core.Repositories;
using Ticketfy.ViewModels.Dialogs;
using System;
using System.Threading.Tasks;
using Serilog;

namespace Ticketfy.ViewModels.Shell;

/// <summary>
/// Orchestrates shift lifecycle: validates active shift on login, opens OpenShift dialog,
/// handles orphaned shift recovery (Z-Cut Ciego), and routes cashup dialogs.
/// Fully decoupled from navigation routing and dialog display.
/// </summary>
public partial class ShiftCoordinatorViewModel : ObservableObject
{
    private readonly IShiftService _shiftService;
    private readonly ISessionManager _sessionManager;
    private readonly IEscPosPrinterService _printerService;
    private readonly IBackupService _backupService;
    private readonly IAttendanceService _attendanceService;

    public event Action<ObservableObject>? ShowDialogRequested;
    public event Action? CloseDialogRequested;
    public event Action? ShiftOpened;
    public event Action? LogoutRequested;

    public ShiftCoordinatorViewModel(
        IShiftService shiftService,
        ISessionManager sessionManager,
        IEscPosPrinterService printerService,
        IBackupService backupService,
        IAttendanceService attendanceService)
    {
        _shiftService = shiftService;
        _sessionManager = sessionManager;
        _printerService = printerService;
        _backupService = backupService;
        _attendanceService = attendanceService;
    }

    /// <summary>
    /// Validates the shift status after login. Returns true if POS is ready to use.
    /// Opens OpenShift dialog or triggers orphaned-shift recovery as needed.
    /// </summary>
    public async Task<bool> ValidateShiftStatusAsync()
    {
        var version = _sessionManager.SessionVersion;
        try
        {
            var activeShift = await _shiftService.GetActiveAsync();
            if (!SessionIsCurrent(version)) return false;

            if (activeShift != null)
            {
                if (DateTime.TryParse(activeShift.StartTime, out var startTime))
                {
                    var localStartTime = Ticketfy.Core.Helpers.DateTimeExtensions.ToBusinessLocalTime(startTime);
                    if (localStartTime.Date < Ticketfy.Core.Helpers.BusinessTime.Today)
                    {
                        return await HandleOrphanedShiftAsync();
                    }
                }
                return true;
            }
            else
            {
                return await PromptOpenShiftAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ShiftCoordinatorViewModel: Error validating shift status");
            return false;
        }
    }

    public void OpenPartialCashup()
    {
        var version = _sessionManager.SessionVersion;
        if (!SessionIsCurrent(version)) return;
        var dialog = new CashupDialogViewModel(_shiftService, _sessionManager, _printerService, _backupService,
            attendanceService: _attendanceService, isFinalZCut: false);
        dialog.RequestClose += () => { if (SessionIsCurrent(version)) CloseDialogRequested?.Invoke(); };
        ShowDialogRequested?.Invoke(dialog);
    }

    public void OpenFinalCashup()
    {
        var version = _sessionManager.SessionVersion;
        if (!SessionIsCurrent(version)) return;
        var dialog = new CashupDialogViewModel(_shiftService, _sessionManager, _printerService, _backupService,
            attendanceService: _attendanceService, isFinalZCut: true);
        dialog.RequestClose += () => { if (SessionIsCurrent(version)) CloseDialogRequested?.Invoke(); };
        ShowDialogRequested?.Invoke(dialog);
    }

    private async Task<bool> HandleOrphanedShiftAsync()
    {
        var version = _sessionManager.SessionVersion;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmVm = new ConfirmDialogViewModel(
            "Turno Suspendido Detectado",
            "Se detectó un turno del día anterior que no fue cerrado correctamente. Debe realizar el Corte final antes de iniciar uno nuevo. ¿Proceder al corte ciego?",
            (confirmed) =>
            {
                if (!SessionIsCurrent(version)) { tcs.TrySetResult(false); return; }
                if (confirmed)
                {
                    var blindCashupVm = new CashupDialogViewModel(_shiftService, _sessionManager,
                        _printerService, _backupService, isFinalZCut: true, isBlindMode: true,
                        attendanceService: _attendanceService);
                    blindCashupVm.RequestClose += async () =>
                    {
                        if (!SessionIsCurrent(version)) { tcs.TrySetResult(false); return; }
                        CloseDialogRequested?.Invoke();
                        tcs.TrySetResult(await ValidateShiftStatusAsync());
                    };
                    ShowDialogRequested?.Invoke(blindCashupVm);
                }
                else
                {
                    CloseDialogRequested?.Invoke();
                    LogoutRequested?.Invoke();
                    tcs.TrySetResult(false);
                }
            }
        );
        return await WaitForDialogAsync(confirmVm, tcs, version);
    }

    private async Task<bool> PromptOpenShiftAsync()
    {
        var version = _sessionManager.SessionVersion;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openShiftVm = new OpenShiftDialogViewModel(_shiftService, _attendanceService, _sessionManager);
        openShiftVm.RequestClose += (success) =>
        {
            if (!SessionIsCurrent(version)) { tcs.TrySetResult(false); return; }
            CloseDialogRequested?.Invoke();
            if (success)
            {
                ShiftOpened?.Invoke();
            }
            // An authenticated operator may enter permitted administrative modules without a shift.
            // Every monetary service independently requires an owned open shift.
            tcs.TrySetResult(success);
        };
        return await WaitForDialogAsync(openShiftVm, tcs, version);
    }

    private bool SessionIsCurrent(long version) => _sessionManager.CurrentCashier is not null
        && !_sessionManager.IsTerminalLocked && version == _sessionManager.SessionVersion;

    private async Task<bool> WaitForDialogAsync(ObservableObject dialog, TaskCompletionSource<bool> decision, long version)
    {
        void CancelChangedSession()
        {
            if (SessionIsCurrent(version)) return;
            decision.TrySetResult(false);
            CloseDialogRequested?.Invoke();
        }
        void CashierChanged(Ticketfy.Core.Models.UserModel? _) => CancelChangedSession();
        void LockChanged(bool _) => CancelChangedSession();
        _sessionManager.CashierChanged += CashierChanged;
        _sessionManager.LockStateChanged += LockChanged;
        try
        {
            if (!SessionIsCurrent(version)) return false;
            ShowDialogRequested?.Invoke(dialog);
            return await decision.Task;
        }
        finally
        {
            _sessionManager.CashierChanged -= CashierChanged;
            _sessionManager.LockStateChanged -= LockChanged;
        }
    }
}
