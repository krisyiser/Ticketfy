using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Base;
using Ticketfy.Core.Services;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

/// <summary>
/// Dialog ViewModel forcing cashier to physically count the drawer and enter initial float.
/// </summary>
public partial class OpenShiftDialogViewModel : ValidatableViewModelBase
{
    private readonly IShiftService _shiftService;
    private readonly string _operationId = Guid.NewGuid().ToString("N");
    private readonly IAttendanceService _attendanceService;
    private readonly ISessionManager _sessionManager;

    [ObservableProperty]
    private string _initialFloatAmount = "0";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public event Action<bool>? RequestClose;

    public OpenShiftDialogViewModel(IShiftService shiftService, IAttendanceService attendanceService, ISessionManager sessionManager)
    {
        _shiftService = shiftService;
        _attendanceService = attendanceService;
        _sessionManager = sessionManager;
    }

    [RelayCommand]
    private async Task OpenShiftAsync()
    {
        if (IsProcessing) return;
        long version = _sessionManager.SessionVersion;
        var cashier = _sessionManager.CurrentCashier;
        if (decimal.TryParse(InitialFloatAmount, out decimal parsedAmount))
        {
            if (parsedAmount < 0)
            {
                ErrorMessage = "El monto inicial no puede ser negativo.";
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(InitialFloatAmount))
        {
            parsedAmount = 0;
        }
        else
        {
            ErrorMessage = "Monto inválido.";
            return;
        }

        try
        {
            IsProcessing = true;
            ErrorMessage = string.Empty;

            var result = await _shiftService.OpenAsync(parsedAmount, _operationId);
            if (version != _sessionManager.SessionVersion) return;
            if (result != null)
            {
                if (cashier != null)
                {
                    try
                    {
                        var attendance = await _attendanceService.ClockInAsync(cashier.Id.ToString());
                        if (!attendance.IsSuccess) Ticketfy.Core.Messages.HardwareFeedbackMessage.Report(
                            "Turno abierto. La asistencia no se confirmó; solicite revisión al supervisor. No repita el fondo inicial.", true);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Shift opened but attendance requires review");
                        Ticketfy.Core.Messages.HardwareFeedbackMessage.Report(
                            "Turno abierto. Falló el registro de asistencia; solicite revisión al supervisor. No repita la apertura.", true);
                    }
                }
                if (version == _sessionManager.SessionVersion) RequestClose?.Invoke(true);
            }
            else
            {
                ErrorMessage = "Error al abrir el turno.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
