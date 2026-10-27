using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Suppliers;

/// <summary>
/// Manages supplier directory CRUD operations with instant search, edit, and delete capabilities.
/// </summary>
public partial class SupplierDirectoryViewModel : ObservableObject
{
    private readonly ISupplierService _supplierService;
    private long _loadVersion;
    private int _page;
    [ObservableProperty] private bool _hasNextPage;
    public bool HasPreviousPage => _page > 0;
    public string PageDescription => $"Página {_page + 1} · {Suppliers.Count} proveedores";

    public ObservableCollection<SupplierDto> Suppliers { get; } = [];

    [ObservableProperty] private string _searchQuery = string.Empty;

    [ObservableProperty] private string _supplierName = string.Empty;
    [ObservableProperty] private string _supplierRfc = string.Empty;
    [ObservableProperty] private string _supplierPhone = string.Empty;
    [ObservableProperty] private string _supplierEmail = string.Empty;
    [ObservableProperty] private string _supplierAddress = string.Empty;
    [ObservableProperty] private string _supplierContact = string.Empty;

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;
    [ObservableProperty] private bool _isEditing = false;
    [ObservableProperty] private string _formTitle = "Registrar Nuevo Proveedor";
    [ObservableProperty] private string _saveButtonText = "GUARDAR PROVEEDOR";

    private SupplierDto? _editingSupplier = null;

    public event Action? SuppliersUpdated;

    public SupplierDirectoryViewModel(ISupplierService supplierService)
    {
        _supplierService = supplierService;
    }

    public async Task LoadSuppliersAsync()
    {
        var version = ++_loadVersion;
        try
        {
            var page = await _supplierService.GetPageAsync(_page, 100, SearchQuery);
            if (version != _loadVersion) return;
            Suppliers.Clear();
            foreach (var supplier in page.Items) Suppliers.Add(supplier);
            HasNextPage = page.HasMore;
            OnPropertyChanged(nameof(HasPreviousPage));
            OnPropertyChanged(nameof(PageDescription));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SupplierDirectoryViewModel: error loading suppliers");
            if (version != _loadVersion) return;
            Suppliers.Clear(); HasNextPage = false;
            IsFeedbackError = true;
            FeedbackMessage = "No se pudieron consultar los proveedores. Verifique su sesión y vuelva a buscar.";
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        _page = 0;
        _ = SearchAfterDelayAsync(++_loadVersion);
    }

    private async Task SearchAfterDelayAsync(long version)
    {
        await Task.Delay(180);
        if (version == _loadVersion) await LoadSuppliersAsync();
    }

    [RelayCommand] private async Task NextPageAsync() { if (HasNextPage) { ++_page; await LoadSuppliersAsync(); } }
    [RelayCommand] private async Task PreviousPageAsync() { if (_page > 0) { --_page; await LoadSuppliersAsync(); } }

    public void ClearSessionData()
    {
        SearchQuery = string.Empty;
        ++_loadVersion;
        _page = 0;
        Suppliers.Clear();
        HasNextPage = false;
        CancelEdit();
        FeedbackMessage = string.Empty;
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(PageDescription));
    }

    [RelayCommand]
    private async Task SaveSupplierAsync()
    {
        if (string.IsNullOrWhiteSpace(SupplierName))
        {
            IsFeedbackError = true;
            FeedbackMessage = "El nombre del proveedor es obligatorio";
            return;
        }

        try
        {
            string cleanPhone = string.IsNullOrWhiteSpace(SupplierPhone) ? string.Empty : new string(SupplierPhone.Where(char.IsDigit).ToArray());

            if (IsEditing && _editingSupplier != null)
            {
                var updatedSupplier = new SupplierDto(
                    _editingSupplier.Id,
                    SupplierName.Trim(),
                    SupplierRfc.Trim(),
                    cleanPhone,
                    SupplierEmail.Trim(),
                    SupplierAddress.Trim(),
                    SupplierContact.Trim(),
                    true
                );

                await _supplierService.UpdateAsync(updatedSupplier);
                IsFeedbackError = false;
                FeedbackMessage = "Proveedor actualizado con éxito";
                CancelEdit();
            }
            else
            {
                var newSupplier = new SupplierDto(
                    Guid.NewGuid().ToString(),
                    SupplierName.Trim(),
                    SupplierRfc.Trim(),
                    cleanPhone,
                    SupplierEmail.Trim(),
                    SupplierAddress.Trim(),
                    SupplierContact.Trim(),
                    true
                );

                await _supplierService.CreateAsync(newSupplier);
                IsFeedbackError = false;
                FeedbackMessage = "Proveedor guardado con éxito";
                ClearForm();
            }

            await LoadSuppliersAsync();
            SuppliersUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SupplierDirectoryViewModel: error saving supplier");
            IsFeedbackError = true;
            FeedbackMessage = "Error al guardar el proveedor";
        }
    }

    [RelayCommand]
    public void StartEdit(SupplierDto supplier)
    {
        if (supplier == null) return;
        _editingSupplier = supplier;
        SupplierName = supplier.Name;
        SupplierRfc = supplier.Rfc ?? string.Empty;
        SupplierPhone = supplier.Phone ?? string.Empty;
        SupplierEmail = supplier.Email ?? string.Empty;
        SupplierAddress = supplier.Address ?? string.Empty;
        SupplierContact = supplier.ContactPerson ?? string.Empty;

        IsEditing = true;
        FormTitle = "Editar Proveedor";
        SaveButtonText = "GUARDAR CAMBIOS";
        FeedbackMessage = string.Empty;
    }

    [RelayCommand]
    public void CancelEdit()
    {
        _editingSupplier = null;
        IsEditing = false;
        FormTitle = "Registrar Nuevo Proveedor";
        SaveButtonText = "GUARDAR PROVEEDOR";
        ClearForm();
    }

    [RelayCommand]
    private async Task DeleteSupplierAsync(SupplierDto supplier)
    {
        if (supplier == null) return;
        if (supplier.Name.ToUpper().Contains("GENERAL") || supplier.Id == "sup_compra_general")
        {
            FeedbackMessage = "No se puede eliminar el proveedor del sistema 'COMPRA GENERAL'.";
            return;
        }

        try
        {
            await _supplierService.DeleteAsync(supplier.Id);
            if (_editingSupplier?.Id == supplier.Id)
            {
                CancelEdit();
            }
            FeedbackMessage = "Proveedor eliminado con éxito";
            await LoadSuppliersAsync();
            SuppliersUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SupplierDirectoryViewModel: error deleting supplier");
            FeedbackMessage = "Error al eliminar el proveedor";
        }
    }

    private void ClearForm()
    {
        SupplierName = string.Empty;
        SupplierRfc = string.Empty;
        SupplierPhone = string.Empty;
        SupplierEmail = string.Empty;
        SupplierAddress = string.Empty;
        SupplierContact = string.Empty;
    }
}
