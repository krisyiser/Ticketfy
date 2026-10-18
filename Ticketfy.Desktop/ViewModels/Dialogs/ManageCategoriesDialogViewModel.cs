using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Implementations;

namespace Ticketfy.ViewModels.Dialogs;

public class CategoryItemViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int ProductCount { get; set; }
    public string ProductCountText => $"({ProductCount} productos)";
    public bool IsNotGeneral => !Name.Equals("General", StringComparison.OrdinalIgnoreCase);
}

public partial class ManageCategoriesDialogViewModel : ObservableObject
{
    private readonly CategoryService _service;
    private CategoryItemViewModel? _editingCategory;
    private string _newId = Guid.NewGuid().ToString("N");
    private bool _saving;
    public ObservableCollection<CategoryItemViewModel> Categories { get; } = [];
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _inputHeader = "Nueva Categoría";
    [ObservableProperty] private string _saveButtonText = "Agregar";
    [ObservableProperty] private bool _isEditing;
    public event Action? RequestClose;
    public event Action? CategoriesUpdated;
    public ManageCategoriesDialogViewModel(CategoryService service) { _service = service; _ = LoadCategoriesAsync(); }
    public async Task LoadCategoriesAsync()
    {
        try
        {
            var categories = await _service.GetAllAsync();
            Categories.Clear();
            foreach (var c in categories) Categories.Add(new() { Id = c.Id, Name = c.Name, ProductCount = c.ProductCount });
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }
    [RelayCommand]
    private async Task SaveCategoryAsync()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            await _service.SaveAsync(_editingCategory?.Id ?? _newId, NewCategoryName);
            _newId = Guid.NewGuid().ToString("N");
            CancelEdit(); await LoadCategoriesAsync(); CategoriesUpdated?.Invoke();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { _saving = false; }
    }
    [RelayCommand]
    public void StartEdit(CategoryItemViewModel item)
    {
        if (!item.IsNotGeneral) return;
        _editingCategory = item; NewCategoryName = item.Name; InputHeader = "Editar Categoría";
        SaveButtonText = "Guardar"; IsEditing = true; ErrorMessage = "";
    }
    [RelayCommand]
    public void CancelEdit()
    {
        _editingCategory = null; NewCategoryName = ""; InputHeader = "Nueva Categoría";
        SaveButtonText = "Agregar"; IsEditing = false; ErrorMessage = "";
    }
    [RelayCommand]
    private async Task DeleteCategoryAsync(CategoryItemViewModel item)
    {
        if (_saving) return;
        _saving = true;
        try { await _service.DeleteAsync(item.Id); CancelEdit(); await LoadCategoriesAsync(); CategoriesUpdated?.Invoke(); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { _saving = false; }
    }
    [RelayCommand] private void Close() => RequestClose?.Invoke();
}
