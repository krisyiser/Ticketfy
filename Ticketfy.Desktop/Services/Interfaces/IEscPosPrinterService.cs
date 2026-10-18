using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface IEscPosPrinterService
{
    Task<bool> PrintTicketAsync(SaleDto sale, string printerPortOrName = "");
    Task<bool> OpenCashDrawerAsync(string printerPortOrName = "", int drawerPin = 0);
    Task<bool> PrintNonSaleCashMovementSlipAsync(ShiftMovementSlipModel model, string printerPortOrName = "");
    Task<bool> PrintTestPageAsync(string printerPortOrName = "");
    Task<bool> IsPrinterOnlineAsync();
    Task<bool> PrintInventoryChecklistAsync(System.Collections.Generic.List<ProductDto> products, string printerPortOrName = "");
    Task<bool> PrintSnapshotChecklistAsync(Ticketfy.Data.Entities.InventorySnapshotEntity snapshot, string printerPortOrName = "");
    Task<bool> PrintPurchaseOrderAsync(Ticketfy.Data.Dtos.PurchaseDto purchase, string printerPortOrName = "");
}
