using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class PromotionsViewModel : ObservableObject
{
    private readonly IPromotionService _promotionService;
    private long _loadVersion;
    private long _sessionVersion;
    private int _page;
    // A delayed click may retain an old row after other rows were edited. Keep its command
    // identity with that row, without retaining every historical row for the lifetime of the screen.
    private System.Runtime.CompilerServices.ConditionalWeakTable<PromotionDto, Dictionary<bool, string>> _statusIntents = new();
    public ObservableCollection<PromotionDto> Promotions { get; } = [];
    public Task InitializationTask { get; }
    public Directories.KitDirectoryViewModel? Kits { get; }
    public Task KitLoadTask { get; private set; } = Task.CompletedTask;
    public bool HasKitDirectory => Kits is not null;
    public bool CanCreateKit => CanEdit && Kits?.CanCreate == true;
    [ObservableProperty] private bool _showKits;
    partial void OnShowKitsChanged(bool value) { if (value && Kits is not null) KitLoadTask = Kits.LoadAsync(); }

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _hasLoadedData;
    public bool CanEdit => !IsLoading && !IsSaving;
    public bool CanNext => CanEdit && HasLoadedData && HasMore;
    public bool CanPrevious => CanEdit && HasLoadedData && _page > 0;
    public string PageDescription => IsLoading ? "Consultando promociones…"
        : HasLoadedData ? $"Página {_page + 1} · {Promotions.Count} promociones" : "Consulta no disponible";

    public event Action? OpenCreateItemKitRequested;
    public event Action? OpenCreatePromotionRequested;
    public event Action<PromotionDto>? OpenEditPromotionRequested;

    public PromotionsViewModel(IPromotionService promotionService, IItemKitService? kitService = null, bool loadOnCreate = true)
    {
        _promotionService = promotionService;
        Kits = kitService is null ? null : new(kitService);
        if (Kits is not null)
        {
            Kits.OpenCreateRequested += () => OpenCreateItemKitRequested?.Invoke();
            Kits.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Kits.CanCreate))
                { OnPropertyChanged(nameof(CanCreateKit)); OpenCreateItemKitDialogCommand.NotifyCanExecuteChanged(); }
            };
        }
        InitializationTask = loadOnCreate ? LoadPromotionsAsync() : Task.CompletedTask;
    }

    public Task LoadPromotionsAsync() => ShowKits && Kits is not null ? Kits.LoadAsync() : LoadPageAsync(0);

    private async Task<bool> LoadPageAsync(int targetPage)
    {
        var version = ++_loadVersion;
        ClearReadData();
        IsLoading = true;
        NotifyNavigation();
        try
        {
            var result = await _promotionService.GetPageAsync(targetPage, 100, SearchQuery);
            bool accepted = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion) return;
                foreach (var item in result.Items) Promotions.Add(item);
                _page = targetPage;
                HasMore = result.HasMore;
                HasLoadedData = true;
                if (IsFeedbackError) { FeedbackMessage = string.Empty; IsFeedbackError = false; }
                accepted = true;
            });
            return accepted;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading promotions");
            if (version == _loadVersion)
            {
                ClearReadData();
                IsFeedbackError = true;
                FeedbackMessage = "No se pudieron consultar las promociones.";
            }
            return false;
        }
        finally
        {
            if (version == _loadVersion) { IsLoading = false; NotifyNavigation(); }
        }
    }

    private void ClearReadData()
    {
        Promotions.Clear(); HasLoadedData = false; HasMore = false;
        NotifyNavigation();
    }

    public void ClearSessionData()
    {
        ShowKits = false; Kits?.ClearSessionData();
        ++_sessionVersion;
        SearchQuery = string.Empty;
        ++_loadVersion;
        _page = 0; IsLoading = false; IsSaving = false;
        _statusIntents = new();
        ClearReadData(); FeedbackMessage = string.Empty; IsFeedbackError = false;
    }

    partial void OnSearchQueryChanged(string value)
    {
        _page = 0; IsLoading = false; ClearReadData();
        _ = SearchAfterDelayAsync(++_loadVersion);
    }

    private async Task SearchAfterDelayAsync(long version)
    {
        await Task.Delay(180);
        if (version == _loadVersion) await LoadPageAsync(0);
    }

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanNext));
        OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(CanCreateKit));
        NextPageCommand.NotifyCanExecuteChanged(); PreviousPageCommand.NotifyCanExecuteChanged();
        ToggleStatusCommand.NotifyCanExecuteChanged(); DeletePromotionCommand.NotifyCanExecuteChanged();
        EditPromotionCommand.NotifyCanExecuteChanged(); OpenAddPromotionDialogCommand.NotifyCanExecuteChanged();
        OpenCreateItemKitDialogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private Task RefreshAsync() => LoadPageAsync(_page);
    [RelayCommand] private Task SearchAsync() => LoadPageAsync(0);
    [RelayCommand(CanExecute = nameof(CanNext))] private Task NextPageAsync() => LoadPageAsync(_page + 1);
    [RelayCommand(CanExecute = nameof(CanPrevious))] private Task PreviousPageAsync() => LoadPageAsync(_page - 1);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void OpenAddPromotionDialog() => OpenCreatePromotionRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void EditPromotion(PromotionDto promotion)
    {
        if (CanEdit && OwnsRow(promotion)) OpenEditPromotionRequested?.Invoke(promotion);
    }

    [RelayCommand(CanExecute = nameof(CanCreateKit))]
    private void OpenCreateItemKitDialog() { if (CanCreateKit) Kits!.CreateCommand.Execute(null); }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ToggleStatusAsync(PromotionDto promo)
    {
        if (!CanEdit || !OwnsRow(promo)) return;
        string operation = StatusOperation(promo, !promo.IsActive);
        await SaveChangeAsync(() => _promotionService.SetActiveAsync(promo.Id, !promo.IsActive, operation),
            promo.IsActive ? "Promoción desactivada" : "Promoción activada");
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DeletePromotionAsync(PromotionDto promo)
    {
        if (!CanEdit || !OwnsRow(promo)) return;
        string operation = StatusOperation(promo, false);
        await SaveChangeAsync(() => _promotionService.DeleteAsync(promo.Id, operation), "Promoción inactivada; se conserva el historial");
    }

    private string StatusOperation(PromotionDto promotion, bool active)
    {
        var intents = _statusIntents.GetValue(promotion, _ => new());
        if (!intents.TryGetValue(active, out var operation))
            intents[active] = operation = Guid.NewGuid().ToString("N");
        return operation;
    }

    private bool OwnsRow(PromotionDto? promotion) => HasLoadedData && promotion is not null
        && Promotions.Any(row => ReferenceEquals(row, promotion));

    private async Task SaveChangeAsync(Func<Task> save, string confirmation)
    {
        long session = _sessionVersion;
        IsSaving = true; NotifyNavigation();
        try
        {
            await save();
            if (session != _sessionVersion) return;
            bool refreshed = await LoadPageAsync(_page);
            if (session != _sessionVersion) return;
            IsFeedbackError = false;
            FeedbackMessage = confirmation + (refreshed ? "" : ". El cambio está guardado, pero falló la consulta. Actualice; no repita el cambio.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error changing promotion");
            if (session == _sessionVersion)
            {
                IsFeedbackError = true;
                FeedbackMessage = Ticketfy.Core.Helpers.UserFacingErrors.Message(ex,
                    "No se pudo confirmar el cambio. Actualice el listado para comprobar el estado de la promoción antes de continuar.");
            }
        }
        finally
        {
            if (session == _sessionVersion) { IsSaving = false; NotifyNavigation(); }
        }
    }
}
