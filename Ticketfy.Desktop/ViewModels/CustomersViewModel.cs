using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class CustomersViewModel : ObservableObject
{
    private readonly ICustomerService _customerService;
    private List<CustomerDto> _allCustomers = [];
    public ObservableCollection<CustomerDto> Customers { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;
    public event Action? OpenAddCustomerRequested;
    public event Action<CustomerDto>? OpenEditCustomerRequested;
    public event Action<CustomerDto>? OpenAddPaymentRequested;
    public event Action<CustomerDto>? OpenStatementRequested;

    public CustomersViewModel(ICustomerService customerService)
    {
        _customerService = customerService;
        _ = LoadCustomersAsync();
    }

    public async Task LoadCustomersAsync()
    {
        try
        {
            var items = await _customerService.GetAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCustomers = new List<CustomerDto>(items);
                ApplySearchFilter();
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customers");
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplySearchFilter();
    }

    private void ApplySearchFilter()
    {
        Customers.Clear();
        var q = SearchQuery?.Trim().ToLowerInvariant() ?? string.Empty;

        foreach (var c in _allCustomers)
        {
            if (string.IsNullOrWhiteSpace(q) ||
                c.Name.ToLowerInvariant().Contains(q) ||
                (!string.IsNullOrEmpty(c.Phone) && c.Phone.Contains(q)) ||
                (!string.IsNullOrEmpty(c.Rfc) && c.Rfc.ToLowerInvariant().Contains(q)) ||
                (!string.IsNullOrEmpty(c.Email) && c.Email.ToLowerInvariant().Contains(q)) ||
                (!string.IsNullOrEmpty(c.CustomerCode) && c.CustomerCode.ToLowerInvariant().Contains(q)))
            {
                Customers.Add(c);
            }
        }
    }

    [RelayCommand]
    private void OpenAddCustomerDialog() => OpenAddCustomerRequested?.Invoke();

    [RelayCommand]
    private void OpenEditCustomerDialog(CustomerDto customer)
    {
        if (customer != null) OpenEditCustomerRequested?.Invoke(customer);
    }

    [RelayCommand]
    private void OpenAddPaymentDialog(CustomerDto customer)
    {
        if (customer != null) OpenAddPaymentRequested?.Invoke(customer);
    }

    [RelayCommand]
    private void OpenStatementDialog(CustomerDto customer)
    {
        if (customer != null) OpenStatementRequested?.Invoke(customer);
    }
}
