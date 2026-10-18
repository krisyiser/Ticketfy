using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public record CustomerLedgerEntryDto(string Date, string Concept, decimal Charge, decimal Payment, decimal Balance);

public partial class CustomerStatementDialogViewModel : ObservableObject
{
    private readonly Ticketfy.Services.Interfaces.ICustomerService _customers;
    private readonly string _customerId;

    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerRfc = string.Empty;
    [ObservableProperty] private decimal _currentDebt;

    public ObservableCollection<CustomerLedgerEntryDto> LedgerEntries { get; } = [];

    public event Action? RequestClose;

    public CustomerStatementDialogViewModel(Ticketfy.Services.Interfaces.ICustomerService customers, string customerId, string customerName, string rfc, decimal currentDebt)
    {
        _customers = customers;
        _customerId = customerId;
        CustomerName = customerName;
        CustomerRfc = rfc;
        CurrentDebt = currentDebt;

        _ = LoadLedgerAsync();
    }

    public async Task LoadLedgerAsync()
    {
        try
        {
            var statement = await _customers.GetStatementAsync(_customerId);
            LedgerEntries.Clear();
            decimal runningBalance = 0m;
            foreach (var entry in statement.Entries)
            {
                runningBalance += entry.Amount;
                string date = DateTimeOffset.TryParse(entry.CreatedAtUtc, out var dt) ? dt.LocalDateTime.ToString("dd/MM/yyyy HH:mm") : entry.CreatedAtUtc;
                LedgerEntries.Add(new(date, entry.EntryType + " · " + entry.DocumentId,
                    Math.Max(0, entry.Amount), Math.Max(0, -entry.Amount), runningBalance));
            }
            CurrentDebt = statement.Balance;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customer statement ledger");
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
