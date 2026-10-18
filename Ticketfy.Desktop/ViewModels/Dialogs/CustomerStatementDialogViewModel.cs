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
    private readonly AppDbContext _db;
    private readonly string _customerId;

    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerRfc = string.Empty;
    [ObservableProperty] private decimal _currentDebt;

    public ObservableCollection<CustomerLedgerEntryDto> LedgerEntries { get; } = [];

    public event Action? RequestClose;

    public CustomerStatementDialogViewModel(AppDbContext db, string customerId, string customerName, string rfc, decimal currentDebt)
    {
        _db = db;
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
            var sales = await _db.Sales
                .AsNoTracking()
                .Where(s => s.CustomerId == _customerId)
                .ToListAsync();

            var payments = await _db.CustomerPayments
                .AsNoTracking()
                .Where(p => p.CustomerId == _customerId)
                .ToListAsync();

            var list = sales.Select(s => new { Date = s.Date, Concept = $"Compra Ticket #{s.Id[..Math.Min(8, s.Id.Length)]}", Charge = s.Total, Payment = 0m })
                .Concat(payments.Select(p => new { Date = p.Date, Concept = "Abono a Cuenta de Crédito", Charge = 0m, Payment = p.Amount }))
                .OrderBy(x => x.Date)
                .ToList();

            LedgerEntries.Clear();
            decimal runningBalance = 0m;
            foreach (var item in list)
            {
                runningBalance += item.Charge - item.Payment;
                string formattedDate = DateTime.TryParse(item.Date, out var dt) ? dt.ToString("dd/MM/yyyy HH:mm") : item.Date;
                LedgerEntries.Add(new CustomerLedgerEntryDto(formattedDate, item.Concept, item.Charge, item.Payment, runningBalance));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading customer statement ledger");
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
