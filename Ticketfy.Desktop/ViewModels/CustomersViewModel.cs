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
    [ObservableProperty] private bool _isLoading;
    private bool _hasLoaded;
    public Task InitializationTask { get; }
    public bool HasPreviousPage => !IsLoading && _hasLoaded && _page > 0;
    public string PageDescription => IsLoading ? "Consultando clientes…" : _hasLoaded ? $"Página {_page + 1} · {Customers.Count} clientes" : "Consulta no disponible";
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
        InitializationTask = loadOnCreate ? LoadCustomersAsync() : Task.CompletedTask;
    }

    public async Task LoadCustomersAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true; _hasLoaded = false; Customers.Clear(); HasNextPage = false; NotifyPage();
        try
        {
            var page = await _customerService.GetPageAsync(_page, 100, SearchQuery);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion) return;
                Customers.Clear();
                foreach (var item in page.Items) Customers.Add(item);
                HasNextPage = page.HasMore; _hasLoaded = true;
                IsFeedbackError = false; FeedbackMessage = "";
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
        finally { if (version == _loadVersion) { IsLoading = false; NotifyPage(); } }
    }

    private void NotifyPage()
    {
        OnPropertyChanged(nameof(HasPreviousPage)); OnPropertyChanged(nameof(PageDescription));
        OpenEditCustomerDialogCommand.NotifyCanExecuteChanged(); OpenAddPaymentDialogCommand.NotifyCanExecuteChanged();
        OpenStatementDialogCommand.NotifyCanExecuteChanged();
    }
    private bool IsCurrentCustomer(CustomerDto? customer) => !IsLoading && _hasLoaded && customer is not null
        && Customers.Any(row => ReferenceEquals(row, customer));
    [RelayCommand] private Task RefreshAsync() => LoadCustomersAsync();

    partial void OnSearchQueryChanged(string value)
    {
        _page = 0; _hasLoaded = false; IsLoading = true; Customers.Clear(); HasNextPage = false; NotifyPage();
        _ = SearchAfterDelayAsync(++_loadVersion);
    }

    private async Task SearchAfterDelayAsync(long version)
    {
        await Task.Delay(180);
        if (version == _loadVersion) await LoadCustomersAsync();
    }

    [RelayCommand] private async Task NextPageAsync() { if (HasNextPage && !IsLoading) { ++_page; await LoadCustomersAsync(); } }
    [RelayCommand] private async Task PreviousPageAsync() { if (_page > 0 && !IsLoading) { --_page; await LoadCustomersAsync(); } }

    public void ClearSessionData()
    {
        SearchQuery = string.Empty;
        ++_loadVersion;
        _page = 0;
        Customers.Clear();
        HasNextPage = false;
        FeedbackMessage = string.Empty; IsFeedbackError = false; _hasLoaded = false; IsLoading = false; NotifyPage();
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(PageDescription));
    }

    [RelayCommand]
    private void OpenAddCustomerDialog() => OpenAddCustomerRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(IsCurrentCustomer))]
    private void OpenEditCustomerDialog(CustomerDto customer)
    {
        if (IsCurrentCustomer(customer)) OpenEditCustomerRequested?.Invoke(customer);
    }

    [RelayCommand(CanExecute = nameof(IsCurrentCustomer))]
    private void OpenAddPaymentDialog(CustomerDto customer)
    {
        if (IsCurrentCustomer(customer)) OpenAddPaymentRequested?.Invoke(customer);
    }

    [RelayCommand(CanExecute = nameof(IsCurrentCustomer))]
    private void OpenStatementDialog(CustomerDto customer)
    {
        if (IsCurrentCustomer(customer)) OpenStatementRequested?.Invoke(customer);
    }
}
