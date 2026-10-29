using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.ViewModels.Navigation;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public record CustomerLedgerEntryDto(string Date, string Concept, decimal Charge, decimal Payment, decimal Balance);

public partial class CustomerStatementDialogViewModel : ObservableObject, IDismissibleDialog
{
    private readonly Ticketfy.Services.Interfaces.ICustomerService _customers;
    private readonly string _customerId;
    private LedgerReadCut? _cut;
    private int _page;
    private int _requestVersion;
    private bool _dismissed;

    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerRfc = string.Empty;
    [ObservableProperty] private decimal _currentDebt;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private bool _hasOlderEntries;
    [ObservableProperty] private bool _hasRecentEntries;
    [ObservableProperty] private string _pageLabel = "";
    [ObservableProperty] private string _status = "";
    public string BalanceDisplay => HasLoaded ? CurrentDebt.ToString("C2") : "No disponible";
    partial void OnCurrentDebtChanged(decimal value) => OnPropertyChanged(nameof(BalanceDisplay));
    partial void OnHasLoadedChanged(bool value) => OnPropertyChanged(nameof(BalanceDisplay));
    public Task InitializationTask { get; }

    public ObservableCollection<CustomerLedgerEntryDto> LedgerEntries { get; } = [];

    public event Action? RequestClose;

    public CustomerStatementDialogViewModel(Ticketfy.Services.Interfaces.ICustomerService customers, string customerId, string customerName, string rfc, decimal currentDebt)
    {
        _customers = customers;
        _customerId = customerId;
        CustomerName = customerName;
        CustomerRfc = rfc;
        // The directory's cached debt is not a verified ledger balance.
        InitializationTask = LoadLedgerAsync();
    }

    public Task LoadLedgerAsync() => LoadPageAsync(0, null);
    [RelayCommand] private Task RefreshLedgerAsync() => LoadLedgerAsync();
    [RelayCommand] private Task OlderEntriesAsync() => HasOlderEntries ? LoadPageAsync(_page + 1, _cut) : Task.CompletedTask;
    [RelayCommand] private Task RecentEntriesAsync() => HasRecentEntries ? LoadPageAsync(_page - 1, _cut) : Task.CompletedTask;

    private async Task LoadPageAsync(int page, LedgerReadCut? cut)
    {
        if (_dismissed || IsBusy) return;
        int request = ++_requestVersion;
        IsBusy = true;
        ClearDisplayedStatement();
        try
        {
            var statement = await _customers.GetStatementPageAsync(_customerId, page, cut: cut);
            if (_dismissed || request != _requestVersion) return;
            decimal runningBalance = statement.OpeningBalance;
            foreach (var entry in statement.Entries)
            {
                runningBalance += entry.Amount;
                string date = Ticketfy.Core.Helpers.BusinessTime.TryLocal(entry.CreatedAtUtc, out var dt) ? dt.ToString("dd/MM/yyyy HH:mm") : entry.CreatedAtUtc;
                LedgerEntries.Add(new(date, EntryLabel(entry.EntryType) + " · " + entry.DocumentId,
                    Math.Max(0, entry.Amount), Math.Max(0, -entry.Amount), runningBalance));
            }
            CurrentDebt = statement.Balance;
            HasLoaded = true;
            _cut = statement.Cut;
            _page = page;
            HasOlderEntries = statement.HasMore;
            HasRecentEntries = page > 0;
            PageLabel = $"Página {page + 1} desde los más recientes · {statement.TotalCount:N0} movimientos";
            Status = LedgerPagePresentation.AccountStatus(statement);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customer statement ledger");
            if (!_dismissed && request == _requestVersion)
            {
                ClearDisplayedStatement();
                Status = "No se pudo consultar el estado de cuenta. Verifique su sesión e intente actualizar; no hay un saldo validado.";
            }
        }
        finally { if (request == _requestVersion) IsBusy = false; }
    }

    private static string EntryLabel(string type) => type switch
    {
        "SALE_CREDIT" => "Venta a crédito",
        "CUSTOMER_PAYMENT" => "Abono de cliente",
        "RETURN_CREDIT" => "Devolución de venta",
        "CREDIT_REFUNDED" => "Devolución de saldo a favor",
        "CREDIT_APPLIED_FROM" => "Saldo a favor aplicado",
        "CREDIT_APPLIED_TO" => "Abono con saldo a favor",
        "HISTORICAL_OPENING" => "Saldo inicial conciliado",
        _ => "Movimiento de cuenta"
    };

    private void ClearDisplayedStatement()
    {
        LedgerEntries.Clear(); CurrentDebt = 0m; HasLoaded = false;
        HasOlderEntries = HasRecentEntries = false; PageLabel = ""; Status = "";
    }

    public void OnDismissed()
    {
        _dismissed = true; ++_requestVersion; IsBusy = false; _cut = null;
        ClearDisplayedStatement();
    }

    [RelayCommand]
    private void Close() { OnDismissed(); RequestClose?.Invoke(); }
}
