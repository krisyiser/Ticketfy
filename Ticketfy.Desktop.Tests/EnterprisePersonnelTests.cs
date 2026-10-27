using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    [Fact]
    public async Task Notes_LegacyAssignmentPreservesUnknownAuthorAndRequiresSupervisor()
    {
        await using var db = Db();
        db.ShiftNotes.Add(new ShiftNoteEntity { Id = "OLD-NOTE", CashierName = "admin", NoteText = "Synthetic historic note", CreatedAt = "2025-01-01" });
        await db.SaveChangesAsync();
        var service = new ShiftNoteService(db, _actor);
        Assert.Empty(await service.GetActiveNotesAsync());
        Assert.Equal("Autor histórico sin verificar", Assert.Single(await service.GetUnassignedHistoricalNotesAsync()).CashierName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssignHistoricalNoteAsync("OLD-NOTE", ""));
        await service.AssignHistoricalNoteAsync("OLD-NOTE", "Se revisó la caja de origen");
        await service.AssignHistoricalNoteAsync("OLD-NOTE", "Se revisó la caja de origen");
        Assert.Empty(await service.GetUnassignedHistoricalNotesAsync());
        Assert.Equal("Autor histórico sin verificar", Assert.Single(await service.GetActiveNotesAsync()).CashierName);
        db.ChangeTracker.Clear();
        var original = await db.ShiftNotes.SingleAsync();
        Assert.Null(original.CashierUserId); Assert.Null(original.ShiftId);
        Assert.Equal("admin", original.CashierName);
        Assert.Equal(2, await db.ShiftNoteRevisions.CountAsync());
        var other = CommercialTestSession.Create(db, false, "OTHER", Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ShiftNoteService(db, other).AssignHistoricalNoteAsync("OLD-NOTE", "reassign"));
        (await db.Users.SingleAsync(u => u.Id == CommercialTestSession.UserId)).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetUnassignedHistoricalNotesAsync());
    }
    [Fact]
    public async Task Attendance_ProductivitySplitsOvernightHoursAcrossTheActualReportDays()
    {
        await using var db = Db();
        db.Attendances.Add(new AttendanceEntity
        {
            UserId = CommercialTestSession.UserId,
            Status = AttendanceStatus.Completed,
            CheckInTime = new DateTime(2026, 10, 17, 22, 0, 0),
            CheckOutTime = new DateTime(2026, 10, 18, 6, 0, 0)
        });
        await db.SaveChangesAsync();
        var reports = new PerformanceAnalyticsService(db, _actor);
        Assert.Equal(2, (await reports.CalculateTrueCashierProductivityAsync(new DateTime(2026, 10, 17), new DateTime(2026, 10, 17))).Single().TotalHoursWorked);
        Assert.Equal(6, (await reports.CalculateTrueCashierProductivityAsync(new DateTime(2026, 10, 18), new DateTime(2026, 10, 18))).Single().TotalHoursWorked);
    }
    [Fact]
    public async Task Attendance_ReviewRequiresEvidencePreservesAuditAndRejectsOverlappingHours()
    {
        await using var db = Db();
        (await db.Shifts.SingleAsync()).IsOpen = 0;
        var start = Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(-4).AddHours(8);
        db.Attendances.AddRange(
            new AttendanceEntity { Id = "REVIEW", UserId = CommercialTestSession.UserId, CheckInTime = start, Status = AttendanceStatus.IncompleteAnomaly },
            new AttendanceEntity { Id = "OVERLAP", UserId = CommercialTestSession.UserId, CheckInTime = start.AddHours(1), Status = AttendanceStatus.ShiftMismatch });
        await db.SaveChangesAsync();
        var service = new AttendanceService(db, _actor);
        Assert.Equal(2, (await service.GetReviewQueueAsync()).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAnomalyAsync("REVIEW", start.AddHours(4), "", "EMPTY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAnomalyAsync("REVIEW", start.AddHours(-1), "evidence", "EARLY"));
        await service.ResolveAnomalyAsync("REVIEW", start.AddHours(4), "Supervisor verified departure", "REVIEW-1");
        await service.ResolveAnomalyAsync("REVIEW", start.AddHours(4), "Supervisor verified departure", "REVIEW-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAnomalyAsync("REVIEW", start.AddHours(5), "changed", "REVIEW-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAnomalyAsync("OVERLAP", start.AddHours(3), "overlap", "REVIEW-2"));
        Assert.Single(await service.GetReviewQueueAsync());
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "ATTENDANCE_REVIEWED"));
        db.ChangeTracker.Clear();
        Assert.Equal(AttendanceStatus.Completed, (await db.Attendances.SingleAsync(a => a.Id == "REVIEW")).Status);
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetReviewQueueAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAnomalyAsync("OVERLAP", start.AddHours(5), "denied", "REVIEW-3"));
    }

    [Fact]
    public async Task Attendance_ReviewCannotCompleteWhileOriginalOperatorHasOpenShift()
    {
        await using var db = Db();
        var start = Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(-4);
        db.Attendances.Add(new AttendanceEntity { Id = "OPEN-SHIFT", UserId = CommercialTestSession.UserId, CheckInTime = start, Status = AttendanceStatus.IncompleteAnomaly });
        await db.SaveChangesAsync();
        var supervisor = CommercialTestSession.Create(db, false, userId: Guid.NewGuid(), role: "SUPERVISOR");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AttendanceService(db, supervisor)
            .ResolveAnomalyAsync("OPEN-SHIFT", start.AddHours(4), "verified", "STILL-OPEN"));
        Assert.Equal(0, await db.TransactionJournal.CountAsync(j => j.EventType == "ATTENDANCE_REVIEWED"));
    }

    [Fact]
    public async Task Attendance_ClosesOnlyOwnShiftAndDoesNotDuplicateEntry()
    {
        await using var db = Db();
        var own = await db.Shifts.SingleAsync();
        own.IsOpen = 0;
        own.EndTime = DateTimeOffset.UtcNow.ToString("O");
        var other = Guid.NewGuid();
        CommercialTestSession.Create(db, register: "OTHER", userId: other, role: "CAJERO");
        var service = new AttendanceService(new TestDbContextFactory(_options), _actor);
        var first = await service.ClockInAsync(CommercialTestSession.UserId.ToString(), "private notes");
        var repeated = await service.ClockInAsync(CommercialTestSession.UserId.ToString());
        Assert.True(first.IsSuccess);
        Assert.Equal(first.AttendanceId, repeated.AttendanceId);
        Assert.True((await service.ClockOutAsync(CommercialTestSession.UserId.ToString())).IsSuccess);
        Assert.False((await service.ClockOutAsync(CommercialTestSession.UserId.ToString())).IsSuccess);
        Assert.Equal(1, await db.Attendances.CountAsync());
        Assert.Equal(2, await db.TransactionJournal.CountAsync(j => j.EntityId == first.AttendanceId));
        Assert.True(await db.Shifts.AnyAsync(s => s.RegisterId == "OTHER" && s.IsOpen == 1));
        Assert.DoesNotContain(await db.TransactionJournal.ToListAsync(), j => j.Details.Contains("private notes"));
    }

    [Fact]
    public async Task Attendance_RejectsAnonymousImpersonationAndOwnOpenRegister()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AttendanceService(db).ClockInAsync(CommercialTestSession.UserId.ToString()));
        var service = new AttendanceService(db, _actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ClockInAsync(Guid.NewGuid().ToString()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetActiveAttendanceAsync(Guid.NewGuid().ToString()));
        await service.ClockInAsync(CommercialTestSession.UserId.ToString());
        Assert.False((await service.ClockOutAsync(CommercialTestSession.UserId.ToString())).IsSuccess);
        Assert.Null((await db.Attendances.SingleAsync()).CheckOutTime);
    }

    [Fact]
    public async Task Attendance_AnomaliesHaveNoFabricatedHoursAndReportDoesNotCountThem()
    {
        await using var db = Db();
        db.Attendances.AddRange(
            new AttendanceEntity { Id = "MISSING", UserId = CommercialTestSession.UserId, CheckInTime = Ticketfy.Core.Helpers.BusinessTime.Now.AddDays(-3) },
            new AttendanceEntity { Id = "CONFIRMED", UserId = CommercialTestSession.UserId, CheckInTime = Ticketfy.Core.Helpers.BusinessTime.Now.AddDays(-2), CheckOutTime = Ticketfy.Core.Helpers.BusinessTime.Now.AddDays(-2).AddHours(4), Status = AttendanceStatus.Completed },
            new AttendanceEntity { Id = "LEGACY-FAKE", UserId = CommercialTestSession.UserId, CheckInTime = Ticketfy.Core.Helpers.BusinessTime.Now.AddDays(-4), CheckOutTime = Ticketfy.Core.Helpers.BusinessTime.Now.AddDays(-4).AddHours(8), Status = AttendanceStatus.IncompleteAnomaly });
        await db.SaveChangesAsync();
        var service = new AttendanceService(db, _actor);
        await service.AutoCloseForgottenAttendancesAsync();
        await service.AutoCloseForgottenAttendancesAsync();
        db.ChangeTracker.Clear();
        var anomaly = await db.Attendances.SingleAsync(a => a.Id == "MISSING");
        Assert.Null(anomaly.CheckOutTime);
        Assert.Equal(AttendanceStatus.IncompleteAnomaly, anomaly.Status);
        Assert.Equal(1, await db.TransactionJournal.CountAsync(j => j.EventType == "ATTENDANCE_ANOMALY"));
        var reports = await new PerformanceAnalyticsService(db, _actor).CalculateTrueCashierProductivityAsync(Ticketfy.Core.Helpers.BusinessTime.Today.AddDays(-7), Ticketfy.Core.Helpers.BusinessTime.Today);
        Assert.Equal(4, reports.Single(r => r.UserId == CommercialTestSession.UserId.ToString()).TotalHoursWorked);
        (await db.Users.SingleAsync()).RoleString = "CAJERO";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AutoCloseForgottenAttendancesAsync());
    }

    [Fact]
    public async Task Notes_UseRealActorKeepRevisionsCancelInsteadOfDeleteAndResolveOnce()
    {
        await using var db = Db();
        var notes = new ShiftNoteService(db, _actor);
        Assert.True(await notes.SaveNoteAsync("FAKE ADMIN", "private customer data"));
        var note = Assert.Single(await notes.GetActiveNotesAsync());
        Assert.Equal("Operador de prueba", note.CashierName);
        Assert.True(await notes.UpdateNoteAsync(note.Id, "new private data", "Caja"));
        Assert.True(await notes.ResolveNoteAsync(note.Id));
        Assert.True(await notes.ResolveNoteAsync(note.Id));
        Assert.True((await notes.GetActiveNotesAsync()).Single().IsResolved);
        Assert.True(await notes.DeleteNoteAsync(note.Id));
        Assert.True(await notes.DeleteNoteAsync(note.Id));
        Assert.Empty(await notes.GetActiveNotesAsync());
        var stored = await db.ShiftNotes.SingleAsync();
        Assert.True(stored.IsCancelled);
        Assert.Equal("SHIFT-1", stored.ShiftId);
        Assert.Equal(4, await db.ShiftNoteRevisions.CountAsync());
        var first = await db.ShiftNoteRevisions.SingleAsync(r => r.Action == "CREATED");
        Assert.Equal("private customer data", first.NoteText);
        Assert.DoesNotContain(await db.TransactionJournal.ToListAsync(), j => j.Details.Contains("private"));
        first.NoteText = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Notes_RequireSessionOwnershipAndRegisterScope()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftNoteService(db).SaveNoteAsync("admin", "bypass"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftNoteService(db).GetActiveNotesAsync());
        var notes = new ShiftNoteService(db, _actor);
        await notes.SaveNoteAsync("", "original");
        var note = (await notes.GetActiveNotesAsync()).Single();
        var peer = CommercialTestSession.Create(db, false, userId: Guid.NewGuid(), role: "CAJERO");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftNoteService(db, peer).UpdateNoteAsync(note.Id, "changed"));
        var supervisor = CommercialTestSession.Create(db, false, userId: Guid.NewGuid(), role: "SUPERVISOR");
        Assert.True(await new ShiftNoteService(db, supervisor).ResolveNoteAsync(note.Id));
        var otherBox = CommercialTestSession.Create(db, false, "OTHER", Guid.NewGuid());
        Assert.Empty(await new ShiftNoteService(db, otherBox).GetActiveNotesAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ShiftNoteService(db, otherBox).DeleteNoteAsync(note.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notes.SaveNoteAsync("", "x", "unknown"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notes.SaveNoteAsync("", new string('x', 2001)));
        Assert.Equal(1, await db.ShiftNotes.CountAsync());
    }
}
