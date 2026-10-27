using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ticketfy.ViewModels.Dialogs;

public partial class ConfigureMinStockDialogViewModel : ObservableObject
{
    [ObservableProperty] private double _minStockValue;
    [ObservableProperty] private string _errorMessage = "";
    private readonly Func<double, System.Threading.Tasks.Task>? _save;

    public event Action<double>? Saved;
    public event Action? RequestClose;

    public ConfigureMinStockDialogViewModel(double currentVal, Func<double, System.Threading.Tasks.Task>? save = null)
    {
        MinStockValue = currentVal;
        _save = save;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task Save()
    {
        try
        {
            if (_save is null) throw new InvalidOperationException("No hay servicio de configuración autorizado.");
            await _save(MinStockValue);
            Saved?.Invoke(MinStockValue);
            RequestClose?.Invoke();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }
}
