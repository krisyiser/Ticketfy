using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Enums;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels.Dialogs;

public partial class PromotionDialogViewModel : ObservableObject
{
    private readonly IPromotionService _promotionService;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _selectedTypeIndex = 0; // 0: Porcentaje (%), 1: Monto Fijo ($), 2: 2x1 Especial

    [ObservableProperty]
    private string _discountValueText = string.Empty;

    [ObservableProperty]
    private bool _isActive = true;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public event Action? RequestClose;

    public PromotionDialogViewModel(IPromotionService promotionService)
    {
        _promotionService = promotionService;
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                ErrorMessage = "El nombre de la promoción u oferta es obligatorio.";
                return;
            }

            // Sanitize discount numeric input
            string rawDiscount = new string((DiscountValueText ?? string.Empty).Where(c => char.IsDigit(c) || c == '.').ToArray());

            if (SelectedTypeIndex == 2) // 2x1 Especial
            {
                var dto2x1 = new PromotionDto(
                    Guid.NewGuid().ToString(),
                    Name.Trim(),
                    0m,
                    IsActive,
                    PromotionType.BuyNGetM,
                    null,
                    "",
                    2.0, // MinQuantity = 2
                    1.0  // FreeQuantity = 1
                );
                await _promotionService.SaveAsync(dto2x1);
                RequestClose?.Invoke();
                return;
            }

            if (string.IsNullOrWhiteSpace(rawDiscount) || !decimal.TryParse(rawDiscount, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal parsedVal) || parsedVal <= 0)
            {
                ErrorMessage = "El valor del descuento es obligatorio y debe ser un número válido mayor a 0.";
                return;
            }

            PromotionType strategy = SelectedTypeIndex == 0 ? PromotionType.PercentageDiscount : PromotionType.FixedAmountDiscount;

            if (strategy == PromotionType.PercentageDiscount && parsedVal > 100)
            {
                ErrorMessage = "El descuento en porcentaje no puede ser mayor al 100%.";
                return;
            }

            ErrorMessage = string.Empty;
            var dto = new PromotionDto(
                Guid.NewGuid().ToString(),
                Name.Trim(),
                parsedVal,
                IsActive,
                strategy,
                null,
                "",
                1.0,
                0.0
            );

            await _promotionService.SaveAsync(dto);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving promotion");
            ErrorMessage = "Error interno al guardar la promoción. Verifique los datos ingresados.";
        }
    }
}
