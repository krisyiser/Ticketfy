using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Security;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels;

public sealed record AttendanceReviewChoice(AttendanceEntity Entry)
{
    public string Label => $"Usuario {Entry.UserId} · Entrada {Entry.CheckInTime:yyyy-MM-dd HH:mm} · {Entry.Status}";
}

public partial class CommercialControlViewModel
{
    private AttendanceService Attendance => new(_factory, _operations);
    [ObservableProperty] private bool _canReviewAttendance;
    [ObservableProperty] private AttendanceReviewChoice? _attendanceReview;
    [ObservableProperty] private string _confirmedAttendanceEnd = "";
    [ObservableProperty] private bool _attendanceEvidenceVerified;
    public ObservableCollection<AttendanceReviewChoice> AttendanceReviews { get; } = [];
    [ObservableProperty] private bool _canReviewNotes;
    [ObservableProperty] private Ticketfy.Data.Dtos.ShiftNoteDto? _historicalNote;
    public ObservableCollection<Ticketfy.Data.Dtos.ShiftNoteDto> HistoricalNotes { get; } = [];

    [RelayCommand]
    private Task ReviewHistoricalNotesAsync() => RunAsync(async () =>
    {
        HistoricalNote = null; HistoricalNotes.Clear();
        foreach (var note in await new ShiftNoteService(_factory, _operations).GetUnassignedHistoricalNotesAsync()) HistoricalNotes.Add(note);
    });

    [RelayCommand]
    private Task AssignHistoricalNoteAsync() => RunAsync(async () =>
    {
        if (HistoricalNote is null) throw new InvalidOperationException("Seleccione una nota histórica y documente el motivo.");
        await new ShiftNoteService(_factory, _operations).AssignHistoricalNoteAsync(HistoricalNote.Id, Reason);
        HistoricalNotes.Remove(HistoricalNote); HistoricalNote = null;
        Message = "Nota asignada a esta caja. El autor histórico permanece sin verificar.";
    });
    partial void OnAttendanceReviewChanged(AttendanceReviewChoice? value)
    { AttendanceEvidenceVerified = false; ConfirmedAttendanceEnd = ""; }

    [RelayCommand]
    private Task ReviewAttendancesAsync() => RunAsync(async () =>
    {
        await Attendance.AutoCloseForgottenAttendancesAsync();
        await LoadAttendanceReviewsAsync();
    });
    private async Task LoadAttendanceReviewsAsync()
    {
        AttendanceReview = null;
        AttendanceReviews.Clear();
        foreach (var entry in await Attendance.GetReviewQueueAsync()) AttendanceReviews.Add(new(entry));
    }
    [RelayCommand]
    private Task ResolveAttendanceAsync() => RunAsync(async () =>
    {
        if (AttendanceReview is null || !AttendanceEvidenceVerified) throw new InvalidOperationException("Seleccione una asistencia y confirme la evidencia de salida.");
        if (!DateTime.TryParseExact(ConfirmedAttendanceEnd, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            throw new InvalidOperationException("Ingrese la salida en hora comercial con formato aaaa-mm-dd hh:mm (24 horas).");
        await Attendance.ResolveAnomalyAsync(AttendanceReview.Entry.Id, end, Reason, Key("ATTENDANCE_REVIEW"));
        AttendanceEvidenceVerified = false;
        await LoadAttendanceReviewsAsync();
    }, "ATTENDANCE_REVIEW");
}
