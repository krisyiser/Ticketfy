using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Helpers;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Navigation;
using Serilog;

namespace Ticketfy.ViewModels.Dialogs;

public partial class PaymentDialogViewModel : ObservableObject, IDismissibleDialog
{
    private readonly ICustomerService _customerService;
    private readonly string _customerId;
    private readonly string _operationId = Guid.NewGuid().ToString("N");
    private bool _saved, _dismissed;
    private CustomerPaymentDto? _pending;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isPendingConfirmation;
    [ObservableProperty] private decimal _currentBalance;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _errorMessage = string.Empty;
    public bool CanEditFields => !IsSaving && !IsPendingConfirmation && !_saved && !_dismissed;
    public bool CanSave => !IsSaving && !_saved && !_dismissed;
    public string SaveButtonText => IsSaving ? "Registrando…" : IsPendingConfirmation ? "Confirmar el mismo abono" : "Registrar abono";
    public event Action? RequestClose;

    public PaymentDialogViewModel(ICustomerService customerService, string customerId, decimal balance = 0m)
    { _customerService = customerService; _customerId = customerId; CurrentBalance = balance; }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanEditFields)); OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(SaveButtonText));
        SaveCommand.NotifyCanExecuteChanged();
    }
    public void OnDismissed() { _dismissed = true; NotifyState(); }
    [RelayCommand]
    private void Cancel()
    {
        if (IsSaving) return;
        OnDismissed(); RequestClose?.Invoke();
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        IsSaving = true; ErrorMessage = ""; NotifyState();
        try
        {
            if (_pending is null)
            {
                if (Amount <= 0 || !NumericEntry.IsMoney(Amount))
                    throw new InvalidOperationException("Indique un abono positivo, dentro del límite permitido y con hasta dos decimales.");
                if (Amount > CurrentBalance)
                    throw new InvalidOperationException("El abono excede el saldo mostrado. Actualice el estado de cuenta antes de continuar.");
                _pending = new(_operationId, _customerId, DateTimeOffset.UtcNow.ToString("O"), Amount);
            }
            IsPendingConfirmation = true; NotifyState();
            await _customerService.AddPaymentAsync(_pending);
            _saved = true; IsPendingConfirmation = false;
            if (_dismissed) return;
            try { RequestClose?.Invoke(); }
            catch (Exception ex)
            { Log.Warning(ex, "Payment committed but dialog refresh failed"); ErrorMessage = "Abono registrado. Cierre y consulte el estado de cuenta; no repita el pago."; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_dismissed)
            { _pending = null; IsPendingConfirmation = false; ErrorMessage = UserFacingErrors.Message(ex); }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Customer payment response uncertain");
            if (!_dismissed) ErrorMessage = "No se pudo confirmar el abono. Pulse «Confirmar el mismo abono» sin cambiar los datos, o cierre y consulte el estado de cuenta. No reciba el dinero otra vez.";
        }
        finally { IsSaving = false; NotifyState(); }
    }
}
