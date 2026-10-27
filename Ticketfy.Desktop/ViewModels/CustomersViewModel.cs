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
    private long _loadVersion;
    private int _page;
    [ObservableProperty] private bool _hasNextPage;
    public bool HasPreviousPage => _page > 0;
    public string PageDescription => $"Página {_page + 1} · {Customers.Count} clientes";
    public ObservableCollection<CustomerDto> Customers { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;
    public event Action? OpenAddCustomerRequested;
    public event Action<CustomerDto>? OpenEditCustomerRequested;
    public event Action<CustomerDto>? OpenAddPaymentRequested;
    public event Action<CustomerDto>? OpenStatementRequested;

    public CustomersViewModel(ICustomerService customerService, bool loadOnCreate = true)
    {
        _customerService = customerService;
        if (loadOnCreate) _ = LoadCustomersAsync();
    }

    public async Task LoadCustomersAsync()
    {
        var version = ++_loadVersion;
        try
        {
            var page = await _customerService.GetPageAsync(_page, 100, SearchQuery);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion) return;
                Customers.Clear();
                foreach (var item in page.Items) Customers.Add(item);
                HasNextPage = page.HasMore;
                OnPropertyChanged(nameof(HasPreviousPage));
                OnPropertyChanged(nameof(PageDescription));
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customers");
            if (version != _loadVersion) return;
            Customers.Clear(); HasNextPage = false;
            IsFeedbackError = true;
            FeedbackMessage = "No se pudieron consultar los clientes. Verifique su sesión y vuelva a buscar.";
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        _page = 0;
        _ = SearchAfterDelayAsync(++_loadVersion);
    }

    private async Task SearchAfterDelayAsync(long version)
    {
        await Task.Delay(180);
        if (version == _loadVersion) await LoadCustomersAsync();
    }

    [RelayCommand] private async Task NextPageAsync() { if (HasNextPage) { ++_page; await LoadCustomersAsync(); } }
    [RelayCommand] private async Task PreviousPageAsync() { if (_page > 0) { --_page; await LoadCustomersAsync(); } }

    public void ClearSessionData()
    {
        SearchQuery = string.Empty;
        ++_loadVersion;
        _page = 0;
        Customers.Clear();
        HasNextPage = false;
        FeedbackMessage = string.Empty;
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(PageDescription));
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
