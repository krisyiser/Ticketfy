using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using System;

namespace Ticketfy.ViewModels.Dialogs;

public partial class BulkWeightDialogViewModel : ObservableObject
{
    private readonly ProductDto _product;
    private bool _isUpdatingInternally = false;

    public ProductDto Product => _product;
    public string ProductName => _product.Name;
    public decimal UnitPrice => _product.Price;
    public string UnitLabel => string.IsNullOrWhiteSpace(_product.Unit) ? "Kg" : _product.Unit;
    public double SubUnitFactor => UnitLabel.ToUpperInvariant() switch
    {
        "MT" or "METROS" or "CENTIMETROS" or "CM" => 100.0,
        _ => 1000.0
    };

    public string PresetSectionTitle => UnitLabel.ToUpperInvariant() switch
    {
        "LT" or "ML" or "LITROS" or "MILILITROS" => "Acceso Rápido por Volumen:",
        "MT" or "CM" or "METROS" or "CENTIMETROS" => "Acceso Rápido por Medida:",
        "KG" or "GR" or "KILOS" or "GRAMOS" => "Acceso Rápido por Peso:",
        _ => "Acceso Rápido por Cantidad:"
    };

    public string QuantityInputLabel => UnitLabel.ToUpperInvariant() switch
    {
        "KG" or "GR" or "KILOS" or "GRAMOS" => "Gramos (g)",
        "LT" or "ML" or "LITROS" or "MILILITROS" => "Mililitros (ml)",
        "MT" or "CM" or "METROS" or "CENTIMETROS" => "Centímetros (cm)",
        _ => $"{UnitLabel}"
    };

    public string Preset1Label => UnitLabel.ToUpperInvariant() switch
    {
        "LT" or "ML" or "LITROS" or "MILILITROS" => "250ml (1/4)",
        "MT" or "CM" or "METROS" or "CENTIMETROS" => "25cm (1/4)",
        "KG" or "GR" or "KILOS" or "GRAMOS" => "250g (1/4)",
        _ => $"0.25 {UnitLabel}"
    };

    public double Preset1Value => UnitLabel.ToUpperInvariant() switch
    {
        "MT" or "CM" or "METROS" or "CENTIMETROS" => 25.0,
        _ => 250.0
    };

    public string Preset2Label => UnitLabel.ToUpperInvariant() switch
    {
        "LT" or "ML" or "LITROS" or "MILILITROS" => "500ml (1/2)",
        "MT" or "CM" or "METROS" or "CENTIMETROS" => "50cm (1/2)",
        "KG" or "GR" or "KILOS" or "GRAMOS" => "500g (1/2)",
        _ => $"0.50 {UnitLabel}"
    };

    public double Preset2Value => UnitLabel.ToUpperInvariant() switch
    {
        "MT" or "CM" or "METROS" or "CENTIMETROS" => 50.0,
        _ => 500.0
    };

    public string Preset3Label => UnitLabel.ToUpperInvariant() switch
    {
        "LT" or "ML" or "LITROS" or "MILILITROS" => "750ml (3/4)",
        "MT" or "CM" or "METROS" or "CENTIMETROS" => "75cm (3/4)",
        "KG" or "GR" or "KILOS" or "GRAMOS" => "750g (3/4)",
        _ => $"0.75 {UnitLabel}"
    };

    public double Preset3Value => UnitLabel.ToUpperInvariant() switch
    {
        "MT" or "CM" or "METROS" or "CENTIMETROS" => 75.0,
        _ => 750.0
    };

    public string Preset4Label => UnitLabel.ToUpperInvariant() switch
    {
        "LT" or "ML" or "LITROS" or "MILILITROS" => $"1000ml (1 {UnitLabel})",
        "MT" or "CM" or "METROS" or "CENTIMETROS" => $"100cm (1 {UnitLabel})",
        "KG" or "GR" or "KILOS" or "GRAMOS" => $"1000g (1 {UnitLabel})",
        _ => $"1 {UnitLabel}"
    };

    public double Preset4Value => UnitLabel.ToUpperInvariant() switch
    {
        "MT" or "CM" or "METROS" or "CENTIMETROS" => 100.0,
        _ => 1000.0
    };

    public string DispatchQuantityDisplay
    {
        get
        {
            double k = QuantityInKilos;
            string unitUpper = UnitLabel.ToUpperInvariant();

            if (k < 1.0 && k > 0)
            {
                if (unitUpper is "KG" or "GR" or "KILOS" or "GRAMOS")
                {
                    double grams = Math.Round(QuantityInGrams, 1);
                    return grams % 1 == 0 ? $"{grams:F0}g" : $"{grams:F1}g";
                }
                else if (unitUpper is "LT" or "ML" or "LITROS" or "MILILITROS")
                {
                    double ml = Math.Round(QuantityInGrams, 1);
                    return ml % 1 == 0 ? $"{ml:F0}ml" : $"{ml:F1}ml";
                }
                else if (unitUpper is "MT" or "CM" or "METROS" or "CENTIMETROS")
                {
                    double cm = Math.Round(QuantityInGrams, 1);
                    return cm % 1 == 0 ? $"{cm:F0}cm" : $"{cm:F1}cm";
                }
            }

            string formattedValue = k.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return $"{formattedValue} {UnitLabel}";
        }
    }

    [ObservableProperty]
    private double _quantityInGrams = 1000.0;

    [ObservableProperty]
    private double _quantityInKilos = 1.0;

    [ObservableProperty]
    private decimal _moneyAmount;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public double ResultQuantity => QuantityInKilos;
    public bool WasConfirmed { get; private set; } = false;

    public event Action<bool, double>? RequestCloseWithResult;

    public BulkWeightDialogViewModel(ProductDto product)
    {
        _product = product;
        _moneyAmount = product.Price;
        _quantityInKilos = 1.0;
        _quantityInGrams = SubUnitFactor;
    }

    partial void OnQuantityInGramsChanged(double value)
    {
        if (_isUpdatingInternally) return;
        _isUpdatingInternally = true;
        try
        {
            QuantityInKilos = Math.Round(value / SubUnitFactor, 4);
            MoneyAmount = Math.Round((decimal)QuantityInKilos * UnitPrice, 2);
            OnPropertyChanged(nameof(DispatchQuantityDisplay));
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    partial void OnQuantityInKilosChanged(double value)
    {
        if (_isUpdatingInternally) return;
        _isUpdatingInternally = true;
        try
        {
            QuantityInGrams = Math.Round(value * SubUnitFactor, 1);
            MoneyAmount = Math.Round((decimal)value * UnitPrice, 2);
            OnPropertyChanged(nameof(DispatchQuantityDisplay));
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    partial void OnMoneyAmountChanged(decimal value)
    {
        if (_isUpdatingInternally || UnitPrice <= 0) return;
        _isUpdatingInternally = true;
        try
        {
            double kilos = (double)Math.Round(value / UnitPrice, 4);
            QuantityInKilos = kilos;
            QuantityInGrams = Math.Round(kilos * SubUnitFactor, 1);
            OnPropertyChanged(nameof(DispatchQuantityDisplay));
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    [RelayCommand]
    private void SetPresetGrams(object? param)
    {
        if (param == null) return;
        if (param is double d)
        {
            QuantityInGrams = d;
        }
        else if (double.TryParse(param.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            QuantityInGrams = parsed;
        }
    }

    [RelayCommand]
    private void SetPresetMoney(object? param)
    {
        if (param == null) return;
        if (param is decimal d)
        {
            MoneyAmount = d;
        }
        else if (decimal.TryParse(param.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal parsed))
        {
            MoneyAmount = parsed;
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (QuantityInKilos <= 0)
        {
            ErrorMessage = "Ingrese una cantidad o peso mayor a cero";
            return;
        }

        WasConfirmed = true;
        RequestCloseWithResult?.Invoke(true, QuantityInKilos);
    }

    [RelayCommand]
    private void Cancel()
    {
        WasConfirmed = false;
        RequestCloseWithResult?.Invoke(false, 0.0);
    }
}
