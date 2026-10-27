using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services;

namespace Ticketfy.ViewModels.Directories;

public partial class KitDirectoryViewModel : PagedDirectoryViewModel<KitDirectoryRow>
{
    private readonly IItemKitService service;
    private long _sessionVersion;
    private KitCapabilities? _capabilities;
    private (string ProductId, bool Active, string Operation)? _pendingStatus;
    [ObservableProperty] private bool _isChanging;
    [ObservableProperty] private string _actionMessage = "";
    public event Action<KitDraft>? OpenEditRequested;
    public event Action? OpenCreateRequested;
    public event Action? CatalogChanged;
    public bool CanCreate => HasLoadedData && !IsLoading && !IsChanging && _pendingStatus is null && _capabilities?.CanCreate == true;
    public bool CanEdit => HasLoadedData && !IsLoading && !IsChanging && _pendingStatus is null
        && OwnsRow(SelectedItem) && _capabilities?.CanEdit == true;
    public bool CanToggle => !IsChanging && (_pendingStatus.HasValue || HasLoadedData && !IsLoading
        && OwnsRow(SelectedItem) && (SelectedItem!.IsActive ? _capabilities?.CanDeactivate : _capabilities?.CanEdit) == true);
    public string StatusButtonText => _pendingStatus.HasValue ? "Confirmar el mismo cambio"
        : SelectedItem?.IsActive == true ? "Desactivar combo" : "Activar combo";

    public KitDirectoryViewModel(IItemKitService service)
    {
        this.service = service;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsLoading) or nameof(HasLoadedData) or nameof(IsChanging)) NotifyActions();
        };
    }
    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanCreate)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(StatusButtonText));
        CreateCommand.NotifyCanExecuteChanged(); EditCommand.NotifyCanExecuteChanged(); ToggleStatusCommand.NotifyCanExecuteChanged();
    }
    protected override void SessionCleared()
    {
        ++_sessionVersion; _pendingStatus = null; _capabilities = null; ActionMessage = ""; IsChanging = false; NotifyActions();
    }
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create() { if (CanCreate) OpenCreateRequested?.Invoke(); }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAsync()
    {
        if (!CanEdit) return;
        var selected = SelectedItem!; long version = _detailVersion, session = _sessionVersion;
        IsChanging = true; ActionMessage = "";
        try
        {
            var draft = await service.GetForEditAsync(selected.ProductId);
            if (session != _sessionVersion || version != _detailVersion || !OwnsRow(selected) || SelectedItem != selected) return;
            if (draft is null) ActionMessage = "El combo ya no existe. Actualice el directorio.";
            else OpenEditRequested?.Invoke(draft);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Kit editor could not be opened");
            if (session == _sessionVersion && version == _detailVersion)
                ActionMessage = ex is KitCommandRejectedException or UnauthorizedAccessException ? ex.Message : "No se pudo abrir el combo. Actualice y vuelva a intentar.";
        }
        finally { if (session == _sessionVersion) IsChanging = false; }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleStatusAsync()
    {
        if (!CanToggle) return;
        long session = _sessionVersion;
        _pendingStatus ??= (SelectedItem!.ProductId, !SelectedItem.IsActive, Guid.NewGuid().ToString("N"));
        var pending = _pendingStatus.Value;
        IsChanging = true; ActionMessage = ""; NotifyActions();
        try
        {
            await service.SetActiveAsync(pending.ProductId, pending.Active, pending.Operation);
            if (session != _sessionVersion) return;
            _pendingStatus = null;
            ActionMessage = pending.Active ? "Activación confirmada." : "Desactivación confirmada; se conserva el historial.";
            try { CatalogChanged?.Invoke(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Kit status confirmed but catalog notification failed"); }
            await LoadAsync();
            if (session == _sessionVersion && !HasLoadedData)
                ActionMessage += " No se pudo actualizar el directorio; el cambio sí quedó confirmado. No lo repita.";
        }
        catch (Exception ex) when (ex is KitCommandRejectedException or UnauthorizedAccessException)
        {
            if (session == _sessionVersion) { _pendingStatus = null; ActionMessage = ex.Message; }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Kit status response uncertain");
            if (session == _sessionVersion)
                ActionMessage = $"No se pudo confirmar el cambio del combo {pending.ProductId}. Use «Confirmar el mismo cambio»; no se enviará otra operación.";
        }
        finally { if (session == _sessionVersion) { IsChanging = false; NotifyActions(); } }
    }

    private long _detailVersion;
    private int _componentPage;
    public ObservableCollection<ItemKitItemDto> Components { get; } = [];
    [ObservableProperty] private string _compositionSummary = "";
    [ObservableProperty] private bool _hasMoreComponents;
    [ObservableProperty] private bool _hasPreviousComponents;
    [ObservableProperty] private bool _isReadingComponents;
    protected override async Task<DirectoryRead<KitDirectoryRow>> ReadAsync(int page, string search, bool inactive)
    {
        long version = _detailVersion, session = _sessionVersion;
        _capabilities = null; NotifyActions();
        var capabilities = await service.GetCapabilitiesAsync();
        var result = await service.GetPageAsync(page, 100, search, inactive);
        if (version == _detailVersion && session == _sessionVersion) { _capabilities = capabilities; NotifyActions(); }
        return new(result.Items, result.HasMore, "Precios y estado del catálogo del POS. La composición se consulta por separado; un combo no representa existencias físicas adicionales.");
    }
    protected override void SelectionChanged()
    {
        ++_detailVersion; _componentPage = 0; Components.Clear(); CompositionSummary = "";
        HasMoreComponents = HasPreviousComponents = IsReadingComponents = false;
        NotifyActions();
    }
    [RelayCommand] private Task ReadComponentsAsync() => LoadComponentsAsync(0);
    [RelayCommand] private Task OlderComponentsAsync() => HasMoreComponents && !IsReadingComponents ? LoadComponentsAsync(_componentPage + 1) : Task.CompletedTask;
    [RelayCommand] private Task PreviousComponentsAsync() => HasPreviousComponents && !IsReadingComponents ? LoadComponentsAsync(_componentPage - 1) : Task.CompletedTask;
    private async Task LoadComponentsAsync(int page)
    {
        var selected = SelectedItem;
        if (!OwnsRow(selected)) return;
        long version = ++_detailVersion;
        Components.Clear(); CompositionSummary = ""; HasMoreComponents = HasPreviousComponents = false; IsReadingComponents = true;
        try
        {
            var result = await service.GetComponentsPageAsync(selected!.Id, page);
            if (version != _detailVersion || !OwnsRow(selected) || SelectedItem != selected) return;
            if (result is null) { CompositionSummary = "El combo ya no está disponible. Actualice el directorio."; return; }
            foreach (var item in result.Items) Components.Add(item);
            _componentPage = page; HasMoreComponents = result.HasMore; HasPreviousComponents = page > 0;
            CompositionSummary = $"{result.Kit.Name} · {result.Kit.Price:C2} · {result.Kit.Status}. "
                + $"Composición página {page + 1}: {Components.Count} de {result.Kit.ComponentCount} partidas. {result.Description}";
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Kit composition could not be read");
            if (version == _detailVersion) CompositionSummary = "No se pudo consultar la composición. No se presenta una receta parcial como completa.";
        }
        finally { if (version == _detailVersion) IsReadingComponents = false; }
    }
}
