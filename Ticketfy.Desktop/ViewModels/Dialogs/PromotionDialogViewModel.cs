using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Enums;

namespace Ticketfy.ViewModels.Dialogs;

public partial class PromotionDialogViewModel : ObservableObject
{
    private readonly IPromotionService _service;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private bool _saving;
    private bool _saved;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _selectedTypeIndex;
    [ObservableProperty] private string _discountValueText = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private ProductDto? _selectedProduct;
    [ObservableProperty] private string _targetCategory = "";
    [ObservableProperty] private bool _allProducts;
    [ObservableProperty] private string _startDateText = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private string _endDateText = DateTime.Today.AddMonths(1).ToString("yyyy-MM-dd");
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
        _initial = initial;
        if (initial is not null)
        {
            _id = initial.Id; Name = initial.Name; SelectedTypeIndex = (int)initial.StrategyType;
            DiscountValueText = initial.DiscountValue.ToString(System.Globalization.CultureInfo.CurrentCulture);
            IsActive = initial.IsActive; TargetCategory = initial.TargetCategory ?? "";
            AllProducts = string.IsNullOrWhiteSpace(initial.TargetProductId) && string.IsNullOrWhiteSpace(TargetCategory);
            StartDateText = (initial.StartDate ?? DateTime.Today).ToString("yyyy-MM-dd");
            EndDateText = (initial.EndDate ?? DateTime.Today.AddMonths(1)).ToString("yyyy-MM-dd");
            Priority = initial.Priority; MinimumQuantity = (decimal)initial.MinQuantity; FreeQuantity = (decimal)initial.FreeQuantity;
        }
        if (products is not null) _ = LoadAsync(products);
    }
    private async Task LoadAsync(IProductService products)
    {
        try
        {
            foreach (var p in await products.GetAllAsync()) Products.Add(p);
            if (_initial?.TargetProductId is not null) SelectedProduct = Products.FirstOrDefault(p => p.Id == _initial.TargetProductId);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }
    [RelayCommand] private void Cancel() => RequestClose?.Invoke();
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_saving || _saved) return;
        _saving = true;
        try
        {
            if (!AllProducts && SelectedProduct is null && string.IsNullOrWhiteSpace(TargetCategory))
                throw new InvalidOperationException("Seleccione un producto, una categoría o Todos los productos.");
            if (!DateTime.TryParseExact(StartDateText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start)
                || !DateTime.TryParseExact(EndDateText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var end) || end < start)
                throw new InvalidOperationException("Revise la vigencia.");
            decimal value = 0m;
            if (SelectedTypeIndex != 2 && (!decimal.TryParse(DiscountValueText, out value) || value <= 0))
                throw new InvalidOperationException("Indique un descuento positivo.");
            if (SelectedTypeIndex is < 0 or > 4 || MinimumQuantity <= 0 || (SelectedTypeIndex == 2 && FreeQuantity <= 0))
                throw new InvalidOperationException("Revise el tipo y las cantidades de la regla.");
            var type = (PromotionType)SelectedTypeIndex;
            var dto = new PromotionDto(_id, Name, value, IsActive, type,
                AllProducts ? null : SelectedProduct?.Id, AllProducts ? "" : TargetCategory.Trim(),
                MinQuantity: (double)MinimumQuantity, FreeQuantity: SelectedTypeIndex == 2 ? (double)FreeQuantity : 0,
                StartDate: start.Date, EndDate: end.Date.AddDays(1).AddTicks(-1), Priority: Priority);
            await _service.SaveAsync(dto);
            _saved = true; RequestClose?.Invoke();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { _saving = false; }
    }
}
