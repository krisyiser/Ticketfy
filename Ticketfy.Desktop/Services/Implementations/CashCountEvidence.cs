using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

internal static class CashCountEvidence
{
    public static void Validate(CashupEntity cashup)
    {
        if (!cashup.Denominations.IsConsistentWith(cashup.ClosedCashAmount))
            throw new InvalidOperationException("Conteo inválido: indique piezas enteras no negativas y un desglose que coincida con el efectivo declarado.");
    }

    private static object?[] Values(string shiftId, CashupEntity cashup, bool legacy) => legacy
        ? [shiftId, cashup.ClosedCashAmount, cashup.Notes]
        : [shiftId, cashup.ClosedCashAmount, cashup.Notes, .. cashup.Denominations.Pieces.Cast<object?>()];

    public static bool Matches(CommandReceiptEntity receipt, string command, string? userId, string? registerId,
        string shiftId, CashupEntity cashup)
    {
        if (receipt.Command != command && receipt.Command != command + "_V2") return false;
        return receipt.Fingerprint == CommandIdentity.Hash([receipt.Command, userId, registerId, shiftId,
            .. Values(shiftId, cashup, receipt.Command == command)]);
    }

    public static async Task<CashupEntity?> BindAsync(AppDbContext db, OperationIdentity actor, string operation,
        string command, string shiftId, CashupEntity cashup)
    {
        var receipt = await db.CommandReceipts.AsNoTracking().SingleOrDefaultAsync(c => c.OperationId == operation);
        var stored = receipt is null ? null : await db.Cashups.AsNoTracking().SingleOrDefaultAsync(c => c.OperationId == operation);
        bool legacy = receipt?.Command == command;
        if (receipt is not null && (stored is null || stored.Denominations != cashup.Denominations
            || stored.ShiftId != shiftId || stored.RegisterId != actor.RegisterId || stored.CashierUserId != actor.UserId
            || stored.ClosedCashAmount != cashup.ClosedCashAmount || stored.Notes != cashup.Notes
            || !Matches(receipt, command, actor.UserId, actor.RegisterId, shiftId, cashup)))
            throw new InvalidOperationException("La clave de operación ya tiene otro conteo o evidencia incompleta. Revise el historial antes de repetir.");
        // Existing commands keep their original fingerprints; only new evidence includes every denomination.
        await CommandIdentity.BindAsync(db, actor, operation, legacy ? command : command + "_V2", Values(shiftId, cashup, legacy));
        return stored;
    }

    public static void CopyRecordedResult(CashupEntity saved, CashupEntity response)
    {
        response.Id = saved.Id;
        response.Timestamp = saved.Timestamp;
        response.Type = saved.Type;
        response.ShiftId = saved.ShiftId;
        response.RegisterId = saved.RegisterId;
        response.CashierUserId = saved.CashierUserId;
        response.CashierName = saved.CashierName;
        response.CashierRole = saved.CashierRole;
        response.OpenCashAmount = saved.OpenCashAmount;
        response.TheoreticalCash = saved.TheoreticalCash;
        response.Difference = saved.Difference;
    }
}
