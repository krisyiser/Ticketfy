using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public partial class ShiftNotesDialogViewModel : ObservableObject
{
    private readonly IShiftNoteService _shiftNoteService;

    [ObservableProperty] private string _cashierName = "CAJERO EN TURNO";
    [ObservableProperty] private string _newNoteText = string.Empty;
    [ObservableProperty] private string _category = "General";
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    // Editing Note State
    [ObservableProperty] private ShiftNoteDto? _editingNote;
    [ObservableProperty] private bool _isEditing = false;

    public ObservableCollection<ShiftNoteDto> Notes { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["General", "Caja", "Inventario", "Urgente", "Clientes"];

    public event Action? RequestClose;

    public ShiftNotesDialogViewModel(IShiftNoteService shiftNoteService)
    {
        _shiftNoteService = shiftNoteService;
        _ = LoadNotesAsync();
    }

    public async Task LoadNotesAsync()
    {
        try
        {
            var list = await _shiftNoteService.GetActiveNotesAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Notes.Clear();
                foreach (var n in list) Notes.Add(n);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading notes in ShiftNotesDialogViewModel");
        }
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (string.IsNullOrWhiteSpace(NewNoteText))
        {
            FeedbackMessage = "Escriba el texto de la nota.";
            return;
        }

        try
        {
            if (IsEditing && EditingNote != null)
            {
                if (!await _shiftNoteService.UpdateNoteAsync(EditingNote.Id, NewNoteText, Category))
                    throw new InvalidOperationException("La nota ya no está disponible para editar.");
                FeedbackMessage = "¡Nota actualizada exitosamente!";
                IsEditing = false;
                EditingNote = null;
            }
            else
            {
                if (!await _shiftNoteService.SaveNoteAsync(CashierName, NewNoteText, Category))
                    throw new InvalidOperationException("No se guardó la nota.");
                FeedbackMessage = "¡Nota guardada correctamente!";
            }

            NewNoteText = string.Empty;
            await LoadNotesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving note");
            FeedbackMessage = ex is UnauthorizedAccessException or InvalidOperationException ? ex.Message : "No se pudo guardar la nota. Intente nuevamente.";
        }
    }

    [RelayCommand]
    private void StartEditNote(ShiftNoteDto note)
    {
        if (note == null) return;
        EditingNote = note;
        NewNoteText = note.NoteText;
        Category = note.Category ?? "General";
        IsEditing = true;
        FeedbackMessage = $"Editando nota de {note.CashierName}";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditingNote = null;
        NewNoteText = string.Empty;
        FeedbackMessage = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(ShiftNoteDto note)
    {
        if (note == null) return;
        try
        {
            FeedbackMessage = await _shiftNoteService.DeleteNoteAsync(note.Id) ? "Nota cancelada; se conserva su historial." : "La nota no está disponible.";
            await LoadNotesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting note");
            FeedbackMessage = ex is UnauthorizedAccessException ? ex.Message : "No se pudo cancelar la nota.";
        }
    }

    [RelayCommand]
    private async Task ToggleResolveNoteAsync(ShiftNoteDto note)
    {
        if (note == null) return;
        try
        {
            await _shiftNoteService.ResolveNoteAsync(note.Id);
            await LoadNotesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error resolving note");
            FeedbackMessage = ex is UnauthorizedAccessException ? ex.Message : "No se pudo resolver la nota.";
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
