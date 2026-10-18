using System.Threading.Tasks;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public interface IPrintDispatcherService
{
    Task DispatchSaleDocumentsAsync(SaleDto sale, bool printReceipt = true);
}
