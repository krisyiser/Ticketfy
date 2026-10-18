using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Data;
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
    private readonly AppDbContext _db;
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
        AppDbContext db,
        IShiftService shiftService,
        ISessionManager sessionManager,
        IEscPosPrinterService printerService,
        IBackupService backupService,
        IAttendanceService attendanceService)
    {
        _db = db;
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
        try
        {
            var activeShift = await _shiftService.GetActiveAsync();

            if (activeShift != null)
            {
                if (DateTime.TryParse(activeShift.StartTime, out var startTime))
                {
                    var localStartTime = startTime.ToLocalTime();
                    if (localStartTime.Date < DateTime.Today)
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
        var dialog = new CashupDialogViewModel(_db, _shiftService, _sessionManager, _printerService, _backupService,
            attendanceService: _attendanceService, isFinalZCut: false);
        dialog.RequestClose += () => CloseDialogRequested?.Invoke();
        ShowDialogRequested?.Invoke(dialog);
    }

    public void OpenFinalCashup()
    {
        var dialog = new CashupDialogViewModel(_db, _shiftService, _sessionManager, _printerService, _backupService,
            attendanceService: _attendanceService, isFinalZCut: true);
        dialog.RequestClose += () => CloseDialogRequested?.Invoke();
        ShowDialogRequested?.Invoke(dialog);
    }

    private async Task<bool> HandleOrphanedShiftAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var confirmVm = new ConfirmDialogViewModel(
            "Turno Suspendido Detectado",
            "Se detectó un turno del día anterior que no fue cerrado correctamente. Debe realizar el Corte final antes de iniciar uno nuevo. ¿Proceder al corte ciego?",
            (confirmed) =>
            {
                if (confirmed)
                {
                    var blindCashupVm = new CashupDialogViewModel(_db, _shiftService, _sessionManager,
                        _printerService, _backupService, isFinalZCut: true, isBlindMode: true,
                        attendanceService: _attendanceService);
                    blindCashupVm.RequestClose += () =>
                    {
                        CloseDialogRequested?.Invoke();
                        _ = ValidateShiftStatusAsync().ContinueWith(t => tcs.TrySetResult(t.Result));
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
        ShowDialogRequested?.Invoke(confirmVm);
        return await tcs.Task;
    }

    private async Task<bool> PromptOpenShiftAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var openShiftVm = new OpenShiftDialogViewModel(_shiftService, _attendanceService, _sessionManager);
        openShiftVm.RequestClose += (success) =>
        {
            CloseDialogRequested?.Invoke();
            if (success)
            {
                ShiftOpened?.Invoke();
            }
            // An authenticated operator may enter permitted administrative modules without a shift.
            // Every monetary service independently requires an owned open shift.
            tcs.TrySetResult(success);
        };
        ShowDialogRequested?.Invoke(openShiftVm);
        return await tcs.Task;
    }
}
