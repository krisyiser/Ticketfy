using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public partial class PaymentDialogViewModel : ObservableObject
{
    private readonly ICustomerService _customerService;
    private readonly string _customerId;
    private readonly string _operationId = Guid.NewGuid().ToString("N");
    private bool _saving;
    private bool _saved;

    [ObservableProperty] private decimal _currentBalance;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public event Action? RequestClose;

    public PaymentDialogViewModel(ICustomerService customerService, string customerId, decimal balance = 0m)
    {
        _customerService = customerService;
        _customerId = customerId;
        CurrentBalance = balance;
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_saving || _saved) return;
        _saving = true;
        try
        {
            if (Amount <= 0)
            {
                ErrorMessage = "El monto a abonar debe ser mayor a 0.";
                return;
            }

            var dto = new CustomerPaymentDto(
                Id: _operationId,
                CustomerId: _customerId,
                Date: DateTimeOffset.UtcNow.ToString("O"),
                Amount: Amount
            );

            await _customerService.AddPaymentAsync(dto);
            _saved = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding payment in PaymentDialogViewModel");
            ErrorMessage = ex.Message;
        }
        finally { _saving = false; }
    }
}
