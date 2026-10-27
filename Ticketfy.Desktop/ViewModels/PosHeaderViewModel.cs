using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Services;
using Ticketfy.Data.Dtos;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

using Ticketfy.Core.State;

namespace Ticketfy.ViewModels;

public partial class PosHeaderViewModel : ObservableObject
{
    private readonly ISessionManager? _sessionManager;
    private readonly IShiftNoteService? _shiftNoteService;
    private readonly CartStateStore _cartStateStore;

    [ObservableProperty] private string _activeCashierName = "Desconocido";
    [ObservableProperty] private bool _isPrinterOk = true;
    [ObservableProperty] private bool _isScannerOk = true;
    [ObservableProperty] private bool _isDbEncrypted = true;

    public ObservableCollection<ShiftNoteDto> ActiveShiftNotes { get; } = [];
    public bool HasShiftNotes => ActiveShiftNotes.Count > 0;
    [ObservableProperty] private string _newShiftNoteText = string.Empty;

    public ObservableCollection<ParkedTicketModel> ParkedTickets { get; } = new();
    public bool HasParkedTickets => ParkedTickets.Count > 0;

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackColor))]
    private bool _feedbackIsError;
    public string FeedbackColor => FeedbackIsError ? "#EF4444" : "#10B981";

    public event Action? ToggleFullscreenRequested;
    public event Action? LogoutRequested;
    public event Action? OpenShiftNotesRequested;
    public event Action? OpenSwitchUserPinRequested;
    public event Action? OpenLockScreenRequested;
    public event Action<string, Action<bool>>? OpenSupervisorPinRequested;
    public event Action? OpenPartialCashupRequested;
    public event Action? OpenFinalCashupRequested;

    private long _noteReadVersion;
    private readonly bool _loadOnSessionChange;
    public PosHeaderViewModel(ISessionManager? sessionManager, IShiftNoteService? shiftNoteService, CartStateStore cartStateStore, bool loadOnCreate = true)
    {
        _sessionManager = sessionManager;
        _shiftNoteService = shiftNoteService;
        _cartStateStore = cartStateStore;
        _loadOnSessionChange = loadOnCreate;

        ParkedTickets.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasParkedTickets));
        ActiveShiftNotes.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasShiftNotes));

        if (_sessionManager != null)
        {
            _sessionManager.CashierChanged += OnCashierChanged;
            if (_sessionManager.CurrentCashier != null)
            {
                OnCashierChanged(_sessionManager.CurrentCashier);
            }
        }

    }

    private void OnCashierChanged(Ticketfy.Core.Models.UserModel? user)
    {
        ++_noteReadVersion;
        ActiveShiftNotes.Clear();
        NewShiftNoteText = string.Empty;
        ActiveCashierName = "Sin sesión";
        if (user == null) return;
        string roleDisplay = user.DisplayRole;
        ActiveCashierName = $"{user.FullName} ({roleDisplay})";
        if (_loadOnSessionChange) _ = LoadActiveShiftNotesAsync();
    }

    [RelayCommand]
    private void SwitchCashier() => OpenSwitchUserPinRequested?.Invoke();

    [RelayCommand]
    private void LockTerminal() => OpenLockScreenRequested?.Invoke();

    [RelayCommand]
    private void ToggleFullscreen() => ToggleFullscreenRequested?.Invoke();

    [RelayCommand]
    private void Logout() => LogoutRequested?.Invoke();

    [RelayCommand]
    private void OpenShiftNotesDialog() => OpenShiftNotesRequested?.Invoke();

    public async Task LoadActiveShiftNotesAsync()
    {
        var session = _sessionManager;
        if (_shiftNoteService == null || session?.CurrentCashier is null || session.IsTerminalLocked) return;
        var version = ++_noteReadVersion;
        var sessionVersion = session.SessionVersion;
        try
        {
            var userId = session.CurrentCashier.Id;
            var list = await _shiftNoteService.GetActiveNotesAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _noteReadVersion || sessionVersion != session.SessionVersion || session.IsTerminalLocked || userId != session.CurrentCashier?.Id) return;
                ActiveShiftNotes.Clear();
                foreach (var n in list)
                {
                    if (!n.IsResolved) ActiveShiftNotes.Add(n);
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (version == _noteReadVersion && sessionVersion == session.SessionVersion) ActiveShiftNotes.Clear(); });
            if (ex is UnauthorizedAccessException) return;
            Log.Error(ex, "Error loading shift notes");
        }
    }

    [RelayCommand]
    private async Task AddShiftNoteAsync()
    {
        if (string.IsNullOrWhiteSpace(NewShiftNoteText) || _shiftNoteService == null || _sessionManager?.CurrentCashier == null)
            return;

        try
        {
            var success = await _shiftNoteService.SaveNoteAsync(
                _sessionManager.CurrentCashier.FullName,
                NewShiftNoteText.Trim(),
                "General"
            );
            if (success)
            {
                NewShiftNoteText = string.Empty;
                await LoadActiveShiftNotesAsync();
            }
            else { FeedbackIsError = true; FeedbackMessage = "No se pudo guardar la nota."; }
        }
        catch (Exception ex)
        {
            FeedbackIsError = true;
            FeedbackMessage = ex is UnauthorizedAccessException or InvalidOperationException ? ex.Message : "No se pudo guardar la nota. Intente nuevamente.";
        }
    }

    [RelayCommand]
    private async Task ResolveShiftNoteAsync(ShiftNoteDto note)
    {
        if (note == null || _shiftNoteService == null) return;
        try
        {
            var success = await _shiftNoteService.ResolveNoteAsync(note.Id);
            if (success)
            {
                ActiveShiftNotes.Remove(note);
            }
        }
        catch (Exception ex)
        {
            FeedbackIsError = true;
            FeedbackMessage = ex is UnauthorizedAccessException ? ex.Message : "No se pudo resolver la nota.";
        }
    }

    [RelayCommand]
    private void ParkCurrentCart()
    {
        if (_cartStateStore.Items.Count == 0) return;

        var parked = new ParkedTicketModel
        {
            TicketId = $"T-{DateTime.Now:HHmmss}",
            TotalAmount = _cartStateStore.Total,
            Lines = _cartStateStore.Items.ToList()
        };
        ParkedTickets.Add(parked);
        _cartStateStore.Clear();
        FeedbackMessage = $"Cuenta pausada: {parked.TicketId}";
    }

    [RelayCommand]
    private void RestoreTicket(ParkedTicketModel ticket)
    {
        if (ticket == null) return;
        _cartStateStore.Clear();
        foreach (var line in ticket.Lines)
        {
            _cartStateStore.Items.Add(line);
        }
        _cartStateStore.RecalculateTotals();
        ParkedTickets.Remove(ticket);
        FeedbackMessage = $"Cuenta {ticket.TicketId} reanudada.";
    }

    [RelayCommand]
    private void DiscardParkedTicket(ParkedTicketModel ticket)
    {
        if (ticket != null)
        {
            ParkedTickets.Remove(ticket);
            FeedbackMessage = $"Cuenta {ticket.TicketId} descartada.";
        }
    }

    [RelayCommand]
    private void GenerateXReport()
    {
        OpenPartialCashupRequested?.Invoke();
    }

    [RelayCommand]
    private void GenerateZReport()
    {
        OpenFinalCashupRequested?.Invoke();
    }

    public void RequestSupervisorPin(string title, Action<bool> callback)
    {
        OpenSupervisorPinRequested?.Invoke(title, callback);
    }
}
