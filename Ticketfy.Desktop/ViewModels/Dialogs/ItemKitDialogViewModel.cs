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
using Ticketfy.Services;
using Ticketfy.ViewModels.Navigation;

namespace Ticketfy.ViewModels.Dialogs;

public partial class ItemKitDialogViewModel : ObservableObject, IDismissibleDialog
{
    private readonly IItemKitService _kitService;
    private readonly IProductService _productService;
    private readonly string _kitId = Guid.NewGuid().ToString("N");
    private bool _saved;
    private bool _dismissed;
    private readonly KitDraft? _initial;
    private KitDraft? _pendingSave;
    private string _operationId = Guid.NewGuid().ToString("N");
    private long _loadVersion;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isPendingConfirmation;
    [ObservableProperty] private bool _hasConflict;
    public bool CanSave => !IsSaving && !_saved && !_dismissed && !HasConflict;
    public bool CanEditFields => CanSave && !IsPendingConfirmation;
    public string Title => _initial is null ? "Nuevo combo / paquete" : "Editar combo / paquete";
    public string SaveButtonText => IsPendingConfirmation ? "Confirmar el mismo guardado" : "Guardar combo";
    public string CloseButtonText => IsPendingConfirmation ? "Cerrar y revisar directorio" : "Cancelar";
    public string ActivationNotice => _initial?.IsActive == false
        ? "Combo inactivo. Guardar la composición no lo activará; la habilitación se cambia desde el directorio." : "";
    public Task InitializationTask { get; }
    [ObservableProperty] private string _productSearch = string.Empty;
    [ObservableProperty] private string _catalogMessage = string.Empty;

    [ObservableProperty] private string _kitBarcode = string.Empty;
    [ObservableProperty] private string _kitName = string.Empty;
    [ObservableProperty] private decimal _kitPrice;
    [ObservableProperty] private string _kitDescription = string.Empty;
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public ObservableCollection<ProductDto> AvailableProducts { get; } = [];
    public ObservableCollection<ItemKitItemDto> DraftKitItems { get; } = [];

    [ObservableProperty] private ProductDto? _selectedProduct;
    [ObservableProperty] private double _itemQuantity = 1.0;

    public event Action? RequestClose;

    public ItemKitDialogViewModel(IItemKitService kitService, IProductService productService, KitDraft? initial = null)
    {
        _kitService = kitService;
        _productService = productService;
        _initial = initial;
        if (initial is not null)
        {
            _kitId = initial.ProductId; KitBarcode = initial.Barcode; KitName = initial.Name;
            KitPrice = initial.Price; KitDescription = initial.Description;
            foreach (var item in initial.Items) DraftKitItems.Add(item);
        }
        InitializationTask = LoadProductsAsync();
    }

    [RelayCommand]
    private async Task LoadProductsAsync()
    {
        if (_dismissed) return;
        var version = ++_loadVersion;
        try
        {
            var page = await _productService.GetPageAsync(0, 100, ProductSearch);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion || _dismissed) return;
                var selected = SelectedProduct;
                AvailableProducts.Clear();
                foreach (var p in page.Items.Where(p => p.Id != _kitId)) AvailableProducts.Add(p);
                SelectedProduct = AvailableProducts.FirstOrDefault(p => p.Id == selected?.Id);
                CatalogMessage = page.HasMore ? "Primeras 100 coincidencias. Refine la búsqueda." : string.Empty;
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading products for ItemKit dialog");
            if (version == _loadVersion && !_dismissed)
            { AvailableProducts.Clear(); SelectedProduct = null; CatalogMessage = "No se pudieron consultar los productos."; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditFields))]
    private void AddItemToDraft()
    {
        if (!CanEditFields) return;
        if (SelectedProduct == null || !AvailableProducts.Contains(SelectedProduct) || SelectedProduct.Id == _kitId
            || !double.IsFinite(ItemQuantity) || ItemQuantity <= 0 || ItemQuantity > (double)(long.MaxValue / 1000m) || Math.Round(ItemQuantity, 3) != ItemQuantity
            || (!SelectedProduct.IsBulk && ItemQuantity != Math.Truncate(ItemQuantity)))
        { FeedbackMessage = "Seleccione un componente y una cantidad válida para su unidad."; return; }

        var existing = DraftKitItems.FirstOrDefault(i => i.ProductId == SelectedProduct.Id);
        if (existing is not null && (!double.IsFinite(existing.Quantity) || existing.Quantity <= 0
            || existing.Quantity > (double)(long.MaxValue / 1000m) || Math.Round(existing.Quantity, 3) != existing.Quantity
            || !SelectedProduct.IsBulk && existing.Quantity != Math.Truncate(existing.Quantity)))
        { FeedbackMessage = "El componente existente tiene una cantidad inválida. Quítelo y agréguelo con la cantidad correcta."; return; }
        decimal quantity = (decimal)ItemQuantity + (decimal)(existing?.Quantity ?? 0);
        if (quantity > long.MaxValue / 1000m || Math.Round(quantity, 3) != quantity || existing is null && DraftKitItems.Count >= 200)
        { FeedbackMessage = "El combo admite hasta 200 componentes con cantidades dentro del rango soportado."; return; }
        if (existing != null)
        {
            DraftKitItems.Remove(existing);
            DraftKitItems.Add(existing with { Quantity = (double)quantity });
        }
        else
        {
            DraftKitItems.Add(new ItemKitItemDto(
                Guid.NewGuid().ToString(),
                string.Empty,
                SelectedProduct.Id,
                SelectedProduct.Name,
                ItemQuantity
            ));
        }

        ItemQuantity = 1.0;
    }

    [RelayCommand(CanExecute = nameof(CanEditFields))]
    private void RemoveItemFromDraft(ItemKitItemDto item)
    {
        if (CanEditFields && item != null) DraftKitItems.Remove(item);
    }

    private KitDraft BuildDraft()
    {
        if (string.IsNullOrWhiteSpace(KitName)) throw new KitCommandRejectedException("El nombre del combo es obligatorio.");
        if (KitPrice < 0 || decimal.Round(KitPrice, 2) != KitPrice)
            throw new KitCommandRejectedException("Indique un precio no negativo con hasta dos decimales.");
        if (DraftKitItems.Count == 0) throw new KitCommandRejectedException("Agregue al menos un componente.");
        if (string.IsNullOrWhiteSpace(KitBarcode))
            KitBarcode = $"COMBO-{_kitId}";
        return new(_kitId, KitBarcode.Trim(), KitName.Trim(), KitPrice, KitDescription, DraftKitItems.ToArray(),
            _initial?.Revision, _initial?.IsActive ?? true);
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveKitAsync()
    {
        if (!CanSave) return;
        IsSaving = true; FeedbackMessage = ""; NotifyState();
        try
        {
            _pendingSave ??= BuildDraft();
            IsPendingConfirmation = true; NotifyState();
            await _kitService.SaveAsync(_pendingSave, _operationId);
            if (_dismissed) return;
            _saved = true; _pendingSave = null; IsPendingConfirmation = false;
            FeedbackMessage = "Combo guardado. Puede cerrar y actualizar el directorio.";
            try { RequestClose?.Invoke(); }
            catch (Exception ex) { Log.Warning(ex, "Kit saved but dialog could not close"); }
        }
        catch (KitConflictException ex)
        {
            if (!_dismissed) { HasConflict = true; _pendingSave = null; IsPendingConfirmation = false; FeedbackMessage = ex.Message; }
        }
        catch (Exception ex) when (ex is KitCommandRejectedException or UnauthorizedAccessException)
        {
            if (!_dismissed)
            {
                _pendingSave = null; IsPendingConfirmation = false; _operationId = Guid.NewGuid().ToString("N");
                FeedbackMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kit save response uncertain");
            if (!_dismissed) FeedbackMessage = "No se pudo confirmar el guardado. Confirme el mismo guardado sin cambiar los datos, o cierre y revise el directorio.";
        }
        finally { IsSaving = false; NotifyState(); }
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanEditFields));
        OnPropertyChanged(nameof(SaveButtonText)); OnPropertyChanged(nameof(CloseButtonText));
        SaveKitCommand.NotifyCanExecuteChanged(); AddItemToDraftCommand.NotifyCanExecuteChanged(); RemoveItemFromDraftCommand.NotifyCanExecuteChanged();
    }
    public void OnDismissed()
    {
        _dismissed = true; ++_loadVersion; _pendingSave = null; IsPendingConfirmation = false;
        AvailableProducts.Clear(); SelectedProduct = null; DraftKitItems.Clear(); FeedbackMessage = CatalogMessage = "";
        NotifyState();
    }

    [RelayCommand]
    private void Cancel() { if (_dismissed) return; OnDismissed(); RequestClose?.Invoke(); }
}
