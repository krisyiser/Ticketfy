using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public partial class CustomerDialogViewModel : ObservableObject
{
    private readonly ICustomerService _customerService;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _phone = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _rfc = string.Empty;

    [ObservableProperty]
    private decimal? _creditLimit;

    [ObservableProperty]
    private string _customerCode = $"CLI-{Random.Shared.Next(1000, 9999)}";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private string? _editingCustomerId;
    private decimal _existingDebt;

    public event Action? RequestClose;

    public CustomerDialogViewModel(ICustomerService customerService)
    {
        _customerService = customerService;
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    public void LoadForEdit(CustomerDto customer)
    {
        _editingCustomerId = customer.Id;
        Name = customer.Name;
        Phone = customer.Phone;
        Email = customer.Email;
        Rfc = customer.Rfc;
        CreditLimit = customer.CreditLimit;
        CustomerCode = customer.CustomerCode;
        _existingDebt = customer.Debt;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                ErrorMessage = "El nombre del cliente es obligatorio.";
                return;
            }

            string cleanPhone = string.IsNullOrWhiteSpace(Phone) ? string.Empty : new string(Phone.Where(char.IsDigit).ToArray());

            if (!string.IsNullOrWhiteSpace(Phone) && cleanPhone.Length != 10)
            {
                ErrorMessage = "El teléfono debe contener 10 dígitos numéricos.";
                return;
            }

            var dto = new CustomerDto(
                Id: _editingCustomerId ?? Guid.NewGuid().ToString(),
                Nombre: Name.Trim(),
                Telefono: cleanPhone,
                Email: Email.Trim(),
                Rfc: Rfc.Trim(),
                LimiteCredito: CreditLimit ?? 0,
                Deuda: _existingDebt,
                CustomerCode: CustomerCode.Trim()
            );

            if (_editingCustomerId != null)
            {
                await _customerService.UpdateAsync(dto);
            }
            else
            {
                await _customerService.AddAsync(dto);
            }
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving customer");
            ErrorMessage = ex.Message;
        }
    }
}
