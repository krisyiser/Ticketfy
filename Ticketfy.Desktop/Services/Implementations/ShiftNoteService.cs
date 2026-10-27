using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class ShiftNoteService : IShiftNoteService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;
    public ShiftNoteService(AppDbContext db, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(db.Options), operations) { }
    public ShiftNoteService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations)
    { _factory = factory; _operations = operations; }

    private Task<OperationIdentity> RequireAsync(AppDbContext db, string permission = PermissionCatalog.PosCheckout) =>
        (_operations ?? throw new UnauthorizedAccessException("Sesión requerida.")).RequireAsync(db, permission, false);

    public async Task<List<ShiftNoteDto>> GetActiveNotesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var actor = await RequireAsync(db);
        return await db.ShiftNotes.AsNoTracking().Where(n => !n.IsCancelled && n.RegisterId == actor.RegisterId)
            .OrderBy(n => n.IsResolved).ThenByDescending(n => n.CreatedAt).ThenBy(n => n.Id).Take(50)
            .Select(n => new ShiftNoteDto(n.Id, n.CashierUserId == null ? "Autor histórico sin verificar" : n.CashierName, n.CreatedAt, n.NoteText, n.Category, n.IsResolved)).ToListAsync();
    }

    public async Task<List<ShiftNoteDto>> GetUnassignedHistoricalNotesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await RequireAsync(db, PermissionCatalog.ShiftNotesManage);
        return await db.ShiftNotes.AsNoTracking().Where(n => n.RegisterId == null && !n.IsCancelled).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Take(100)
            .Select(n => new ShiftNoteDto(n.Id, "Autor histórico sin verificar", n.CreatedAt, n.NoteText, n.Category, n.IsResolved)).ToListAsync();
    }

    public async Task AssignHistoricalNoteAsync(string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new InvalidOperationException("Documente el motivo de asignar esta nota histórica a la caja actual.");
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireAsync(db, PermissionCatalog.ShiftNotesManage);
        var note = await db.ShiftNotes.SingleAsync(n => n.Id == id);
        if (note.RegisterId == actor.RegisterId) return;
        if (note.RegisterId is not null || note.IsCancelled) throw new InvalidOperationException("La nota ya está asignada o cancelada. No se puede reasignar a otra caja.");
        RecordRevision(db, actor, note, "PREVIOUS");
        note.RegisterId = actor.RegisterId;
        // Unknown historical author/shift remain unknown; approval is recorded independently.
        RecordRevision(db, actor, note, "ASSIGNED");
        CommercialLedger.Journal(db, actor, "NOTE-ASSIGN:" + id, "HISTORICAL_NOTE_ASSIGNED", id, 0m, "Autor y turno históricos no inferidos. Motivo: " + reason.Trim());
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static void Validate(string text, string category)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
            throw new InvalidOperationException("Escriba una nota de hasta 2000 caracteres.");
        if (category is not ("General" or "Caja" or "Inventario" or "Urgente" or "Clientes"))
            throw new InvalidOperationException("Categoría de nota no válida.");
    }

    public async Task<bool> SaveNoteAsync(string cashierName, string noteText, string category = "General")
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireAsync(db);
        Validate(noteText, category);
        var userId = Guid.Parse(actor.UserId);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        var shift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.RegisterId == actor.RegisterId
            && s.CashierUserId == actor.UserId && s.IsOpen == 1);
        actor = actor with { ShiftId = shift?.Id };
        var note = new ShiftNoteEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            CashierName = user.FullName,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            NoteText = noteText.Trim(),
            Category = category
        };
        db.ShiftNotes.Add(note);
        RecordRevision(db, actor, note, "CREATED");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    public Task<bool> UpdateNoteAsync(string id, string noteText, string category = "General") =>
        ChangeAsync(id, "UPDATED", noteText, category);
    public Task<bool> DeleteNoteAsync(string id) => ChangeAsync(id, "CANCELLED");
    // Explicit and idempotent: repeated clicks never reopen a resolved note.
    public Task<bool> ResolveNoteAsync(string id) => ChangeAsync(id, "RESOLVED");

    private async Task<bool> ChangeAsync(string id, string action, string? text = null, string? category = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var actor = await RequireAsync(db);
        var note = await db.ShiftNotes.SingleOrDefaultAsync(n => n.Id == id);
        if (note is null) return false;
        if (note.RegisterId != actor.RegisterId)
            throw new UnauthorizedAccessException("La nota no pertenece a esta caja.");
        if (note.CashierUserId != actor.UserId) await RequireAsync(db, PermissionCatalog.ShiftNotesManage);
        actor = actor with
        {
            ShiftId = (await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s =>
            s.RegisterId == actor.RegisterId && s.CashierUserId == actor.UserId && s.IsOpen == 1))?.Id
        };
        if (note.IsCancelled) return action == "CANCELLED";
        if (action == "RESOLVED" && note.IsResolved) return true;
        if (!await db.ShiftNoteRevisions.AnyAsync(r => r.NoteId == id)) RecordRevision(db, actor, note, "PREVIOUS");
        if (action == "UPDATED")
        {
            Validate(text!, category!);
            if (note.NoteText == text!.Trim() && note.Category == category) return true;
            note.NoteText = text.Trim();
            note.Category = category!;
        }
        else if (action == "CANCELLED") note.IsCancelled = true;
        else note.IsResolved = true;
        RecordRevision(db, actor, note, action);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    private static void RecordRevision(AppDbContext db, OperationIdentity actor, ShiftNoteEntity note, string action)
    {
        var revision = new ShiftNoteRevisionEntity
        {
            NoteId = note.Id,
            Action = action,
            NoteText = note.NoteText,
            Category = note.Category,
            CashierUserId = actor.UserId,
            RegisterId = actor.RegisterId,
            ShiftId = actor.ShiftId
        };
        db.ShiftNoteRevisions.Add(revision);
        // Free text and names stay in the access-controlled DB, not general logs or journal details.
        CommercialLedger.Journal(db, actor, "NOTE:" + revision.Id, "SHIFT_NOTE_" + action, note.Id, 0m, "Revisión " + revision.Id);
    }
}
