using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ticketfy.ViewModels.Directories;

public sealed record DirectoryRead<T>(List<T> Items, bool HasMore, string Summary);

/// <summary>One visible page, explicit refresh, and no data/feedback from superseded requests.</summary>
public abstract partial class PagedDirectoryViewModel<T> : ObservableObject where T : class
{
    private long _requestVersion;
    private int _page;
    public ObservableCollection<T> Rows { get; } = [];
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _includeInactive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _hasLoadedData;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private T? _selectedItem;
    public bool CanNext => !IsLoading && HasLoadedData && HasMore;
    public bool CanPrevious => !IsLoading && HasLoadedData && _page > 0;
    public string PageLabel => IsLoading ? "Consultando…" : HasLoadedData
        ? $"Página {_page + 1} · {Rows.Count} registros visibles" : "Consulta no disponible";
    protected abstract Task<DirectoryRead<T>> ReadAsync(int page, string search, bool inactive);
    protected virtual void SelectionChanged() { }
    protected virtual void SessionCleared() { }
    protected bool OwnsRow(T? row) => HasLoadedData && row is not null && Rows.Any(r => ReferenceEquals(r, row));
    partial void OnSelectedItemChanged(T? value) => SelectionChanged();
    partial void OnSearchQueryChanged(string value) => Invalidate();
    partial void OnIncludeInactiveChanged(bool value) => Invalidate();

    private void ClearRead()
    {
        SelectedItem = null; Rows.Clear(); HasMore = HasLoadedData = false;
        Summary = ErrorMessage = "";
        SelectionChanged(); NotifyPage();
    }
    private void Invalidate()
    {
        ++_requestVersion; _page = 0; IsLoading = false; ClearRead();
    }
    public void ClearSessionData()
    {
        SearchQuery = ""; IncludeInactive = false; Invalidate(); SessionCleared();
    }
    public Task LoadAsync() => LoadPageAsync(0);
    [RelayCommand] private Task SearchAsync() => LoadPageAsync(0);
    [RelayCommand] private Task RefreshAsync() => LoadPageAsync(_page);
    [RelayCommand(CanExecute = nameof(CanNext))] private Task NextAsync() => CanNext ? LoadPageAsync(_page + 1) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanPrevious))] private Task PreviousAsync() => CanPrevious ? LoadPageAsync(_page - 1) : Task.CompletedTask;
    private async Task LoadPageAsync(int page)
    {
        long version = ++_requestVersion;
        string search = SearchQuery; bool inactive = IncludeInactive;
        ClearRead(); IsLoading = true; NotifyPage();
        try
        {
            var result = await ReadAsync(page, search, inactive);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _requestVersion) return;
                foreach (var row in result.Items) Rows.Add(row);
                _page = page; HasLoadedData = true; HasMore = result.HasMore; Summary = result.Summary;
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Commercial directory could not be read");
            if (version == _requestVersion)
            { ClearRead(); ErrorMessage = "No se pudo consultar el directorio. Revise su sesión y vuelva a intentar; no hay saldos ni filas verificados."; }
        }
        finally { if (version == _requestVersion) { IsLoading = false; NotifyPage(); } }
    }
    private void NotifyPage()
    {
        OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(CanPrevious)); OnPropertyChanged(nameof(PageLabel));
        NextCommand.NotifyCanExecuteChanged(); PreviousCommand.NotifyCanExecuteChanged();
    }
}
