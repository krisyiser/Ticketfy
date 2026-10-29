using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public partial class CustomerDialogViewModel : ObservableObject, Ticketfy.ViewModels.Navigation.IDismissibleDialog
{
    private readonly string _newId = Guid.NewGuid().ToString("N");
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isPendingConfirmation;
    private bool _dismissed;
    private CustomerDto? _pending;
    public CustomerDto? SavedCustomer { get; private set; }
    public string Title => _editingCustomerId is null ? "Nuevo cliente" : "Editar cliente";
    public bool CanEditFields => !IsSaving && !IsPendingConfirmation && !_saved && !_dismissed;
    public bool CanSave => !IsSaving && !_saved && !_dismissed && (!IsPendingConfirmation || _editingCustomerId is null);
    public string SaveButtonText => IsSaving ? "Guardando…" : IsPendingConfirmation ? "Confirmar la misma alta" : "Guardar cliente";
    private bool _saved;
    private double _existingPoints;
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
    private string _customerCode = "CLI-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

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
        if (IsSaving) return;
        OnDismissed(); RequestClose?.Invoke();
    }

    public void OnDismissed() { _dismissed = true; NotifyState(); }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanEditFields)); OnPropertyChanged(nameof(SaveButtonText));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void LoadForEdit(CustomerDto customer)
    {
        _existingPoints = customer.PuntosSaldo;
        _editingCustomerId = customer.Id;
        Name = customer.Name;
        Phone = customer.Phone;
        Email = customer.Email;
        Rfc = customer.Rfc;
        CreditLimit = customer.CreditLimit;
        CustomerCode = customer.CustomerCode;
        _existingDebt = customer.Debt;
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        IsSaving = true; ErrorMessage = ""; NotifyState();
        try
        {
            _pending ??= Ticketfy.Core.Helpers.CustomerInput.Normalize(new CustomerDto(
                _editingCustomerId ?? _newId, Name, Phone, Email, Rfc, CreditLimit ?? 0m,
                _existingDebt, _existingPoints, CustomerCode: CustomerCode));
            var dto = _pending;
            IsPendingConfirmation = true; NotifyState();

            if (_editingCustomerId != null)
            {
                await _customerService.UpdateAsync(dto);
            }
            else
            {
                await _customerService.AddAsync(dto);
            }
            _saved = true; SavedCustomer = dto; IsPendingConfirmation = false;
            if (_dismissed) return;
            try { RequestClose?.Invoke(); }
            catch (Exception ex)
            { Log.Warning(ex, "Customer saved but dialog refresh failed"); ErrorMessage = "Cliente guardado. Cierre y actualice el directorio."; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException
            || ex.GetBaseException() is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            Log.Warning(ex, "Customer save rejected");
            if (!_dismissed)
            { _pending = null; IsPendingConfirmation = false; ErrorMessage = Ticketfy.Core.Helpers.UserFacingErrors.Message(ex); }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Customer save response uncertain");
            if (!_dismissed) ErrorMessage = _editingCustomerId is null
                ? "No se pudo confirmar el alta. Confirme la misma alta sin cambiar los datos, o cierre y revise el directorio."
                : "No se pudo confirmar la edición. Cierre y vuelva a consultar el cliente antes de editarlo otra vez.";
        }
        finally { IsSaving = false; NotifyState(); }
    }
}
