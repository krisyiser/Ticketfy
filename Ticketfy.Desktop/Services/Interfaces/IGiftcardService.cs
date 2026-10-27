using Ticketfy.Data.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ticketfy.Services.Interfaces;

public interface IGiftcardService
{
    Task<Ticketfy.Data.Dtos.AccountStatementPageDto> GetStatementPageAsync(string cardNumber, int page = 0, int size = 100,
        Ticketfy.Data.Dtos.LedgerReadCut? cut = null) => throw new System.NotSupportedException();
    Task<List<GiftcardEntity>> GetAllAsync();
    Task<Ticketfy.Data.Dtos.WalletDirectoryPage> GetPageAsync(int page, int size = 100, string? search = null, bool includeInactive = false);
    Task<GiftcardEntity?> GetByCardNumberAsync(string cardNumber);
    Task<bool> DeductBalanceAsync(string cardNumber, decimal amount);
    Task<(bool IsValid, decimal Balance, string Error)> ValidateCardAsync(string cardNumber);
    Task<bool> RedeemBalanceAsync(string cardNumber, decimal amountToRedeem, string saleId = "");
    Task CreateCardAsync(string cardNumber, decimal initialBalance, string? customerId = null);
    Task RechargeAsync(string cardId, decimal amount, Ticketfy.Core.Enums.PaymentMethod method, string activeShiftId);
    Task RechargeAsync(string cardId, decimal amount, Ticketfy.Core.Enums.PaymentMethod method, string activeShiftId, string operationId) => throw new System.NotSupportedException();
}
