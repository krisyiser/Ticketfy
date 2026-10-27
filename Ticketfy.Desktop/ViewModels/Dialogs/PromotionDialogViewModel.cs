using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Enums;
using Ticketfy.Services;
using Ticketfy.ViewModels.Navigation;
using System.Globalization;

namespace Ticketfy.ViewModels.Dialogs;

public partial class PromotionDialogViewModel : ObservableObject, IDismissibleDialog
{
    private readonly IPromotionService _service;
    private readonly IProductService? _products;
    private long _loadVersion;
    private bool _initialSelectionLoaded;
    [ObservableProperty] private string _productSearch = string.Empty;
    [ObservableProperty] private string _catalogMessage = string.Empty;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private bool _saved;
    private bool _dismissed;
    private PromotionDto? _pendingSave;
    private string _operationId = Guid.NewGuid().ToString("N");
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isPendingConfirmation;
    [ObservableProperty] private bool _hasConflict;
    public bool CanSave => !IsSaving && !_saved && !_dismissed && !HasConflict;
    public bool CanEditFields => !IsSaving && !IsPendingConfirmation && !_saved && !_dismissed;
    public string SaveButtonText => IsPendingConfirmation ? "Confirmar el mismo guardado" : "Guardar oferta";
    public string CloseButtonText => IsPendingConfirmation ? "Cerrar y revisar directorio" : "Cancelar";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _selectedTypeIndex;
    [ObservableProperty] private string _discountValueText = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private ProductDto? _selectedProduct;
    [ObservableProperty] private string _targetCategory = "";
    [ObservableProperty] private bool _allProducts;
    [ObservableProperty] private string _startDateText = Ticketfy.Core.Helpers.BusinessTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private string _endDateText = Ticketfy.Core.Helpers.BusinessTime.Today.AddMonths(1).ToString("yyyy-MM-dd");
    [ObservableProperty] private int _priority;
    [ObservableProperty] private decimal _minimumQuantity = 1m;
    [ObservableProperty] private decimal _freeQuantity = 1m;
    public string Title => _initial is null ? "Nueva promoción" : "Editar promoción";
    private readonly PromotionDto? _initial;
    public ObservableCollection<ProductDto> Products { get; } = [];
    public event Action? RequestClose;
    public PromotionDialogViewModel(IPromotionService service, IProductService? products = null, PromotionDto? initial = null)
    {
        _service = service;
        _products = products;
        _initial = initial;
        if (initial is not null)
        {
            _id = initial.Id; Name = initial.Name; SelectedTypeIndex = (int)initial.StrategyType;
            DiscountValueText = initial.DiscountValue.ToString(System.Globalization.CultureInfo.CurrentCulture);
            IsActive = initial.IsActive; TargetCategory = initial.TargetCategory ?? "";
            AllProducts = string.IsNullOrWhiteSpace(initial.TargetProductId) && string.IsNullOrWhiteSpace(TargetCategory);
            StartDateText = initial.StartDate.HasValue ? FormatDate(initial.StartDate.Value) : "";
            EndDateText = initial.EndDate.HasValue ? FormatDate(initial.EndDate.Value) : "";
            ErrorMessage = initial.ReadWarning ?? "";
            Priority = initial.Priority; MinimumQuantity = (decimal)initial.MinQuantity; FreeQuantity = (decimal)initial.FreeQuantity;
        }
        if (products is not null) _ = SearchProductsAsync();
    }
    [RelayCommand]
    private async Task SearchProductsAsync()
    {
        if (_products is null || _dismissed) return;
        var version = ++_loadVersion;
        try
        {
            var page = await _products.GetPageAsync(0, 100, ProductSearch);
            ProductDto? initial = null;
            if (!_initialSelectionLoaded && _initial?.TargetProductId is not null)
                initial = await _products.GetByIdAsync(_initial.TargetProductId);
            if (version != _loadVersion || _dismissed) return;
            var selected = SelectedProduct ?? initial;
            _initialSelectionLoaded = true;
            Products.Clear();
            foreach (var product in page.Items) Products.Add(product);
            if (selected is not null && Products.All(p => p.Id != selected.Id)) Products.Add(selected);
            SelectedProduct = Products.FirstOrDefault(p => p.Id == selected?.Id);
            CatalogMessage = page.HasMore ? "Primeras 100 coincidencias. Refine la búsqueda." : string.Empty;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Promotion product lookup failed");
            if (version == _loadVersion && !_dismissed) ErrorMessage = "No se pudieron consultar los productos.";
        }
    }
    public void OnDismissed()
    {
        _dismissed = true; ++_loadVersion; _pendingSave = null; IsPendingConfirmation = false;
        NotifyState();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_dismissed) return;
        OnDismissed(); RequestClose?.Invoke();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(CanEditFields)); OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(CloseButtonText));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static string FormatDate(DateTime value) => value.ToString(
        value.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string text, bool end)
    {
        text = text.Trim();
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"];
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new InvalidOperationException("Revise la vigencia: use AAAA-MM-DD y, si necesita hora, HH:mm:ss. Las horas corresponden a la zona comercial.");
        return end && text.Length == 10 ? value.Date.AddTicks(TimeSpan.TicksPerDay - 1) : value;
    }

    private PromotionDto BuildDraft()
    {
        if (!AllProducts && SelectedProduct is null && string.IsNullOrWhiteSpace(TargetCategory))
            throw new InvalidOperationException("Seleccione un producto, una categoría o Todos los productos.");
        var start = ParseDate(StartDateText, false);
        var end = ParseDate(EndDateText, true);
        if (end < start) throw new InvalidOperationException("El fin de vigencia no puede ser anterior al inicio.");
        decimal value = 0m;
        if (SelectedTypeIndex != 2 && (!decimal.TryParse(DiscountValueText, out value) || value <= 0))
            throw new InvalidOperationException("Indique un descuento positivo.");
        if (SelectedTypeIndex is < 0 or > 4 || MinimumQuantity <= 0 || (SelectedTypeIndex == 2 && FreeQuantity <= 0))
            throw new InvalidOperationException("Revise el tipo y las cantidades de la regla.");
        return new PromotionDto(_id, Name, value, IsActive, (PromotionType)SelectedTypeIndex,
            AllProducts ? null : SelectedProduct?.Id, AllProducts ? "" : TargetCategory.Trim(),
            MinQuantity: (double)MinimumQuantity, FreeQuantity: SelectedTypeIndex == 2 ? (double)FreeQuantity : 0,
            StartDate: start, EndDate: end, Priority: Priority, Revision: _initial?.Revision);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        IsSaving = true; ErrorMessage = ""; NotifyState();
        try
        {
            _pendingSave ??= BuildDraft();
            IsPendingConfirmation = true; NotifyState();
            await _service.SaveAsync(_pendingSave, _operationId);
            if (_dismissed) return;
            _saved = true; _pendingSave = null; IsPendingConfirmation = false;
            try { RequestClose?.Invoke(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Promotion saved but dialog could not close"); ErrorMessage = "Oferta guardada. Cierre y actualice el directorio; no repita el guardado."; }
        }
        catch (PromotionConflictException ex)
        {
            if (!_dismissed) { HasConflict = true; _pendingSave = null; IsPendingConfirmation = false; ErrorMessage = ex.Message; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_dismissed)
            {
                _pendingSave = null; IsPendingConfirmation = false; _operationId = Guid.NewGuid().ToString("N");
                ErrorMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Promotion save response uncertain");
            if (!_dismissed) ErrorMessage = "No se pudo confirmar el guardado. Use «Confirmar el mismo guardado» sin cambiar los datos; también puede cerrar y comprobar el directorio.";
        }
        finally { IsSaving = false; NotifyState(); }
    }
}
