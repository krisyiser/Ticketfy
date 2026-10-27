using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Base;

namespace Ticketfy.ViewModels;

public partial class SetupBusinessDataViewModel : ValidatableViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly Action _navigateToNextStep;
    private readonly Action? _navigateToPreviousStep;
    private bool _ready, _departed;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _businessName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    public Task InitializationTask { get; }
    public bool CanEdit => _ready && !IsWorking && !_departed;
    public bool CanLeave => !IsWorking && !_departed;

    public SetupBusinessDataViewModel(ISettingsService settingsService, Action navigateToNextStep, Action? navigateToPreviousStep = null)
    {
        _settingsService = settingsService;
        _navigateToNextStep = navigateToNextStep;
        _navigateToPreviousStep = navigateToPreviousStep;
        InitializationTask = ReloadAsync();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanLeave));
        SaveAndContinueCommand.NotifyCanExecuteChanged(); GoBackCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsWorkingChanged(bool value) => NotifyState();
    public void Deactivate() { _departed = true; _ready = false; BusinessName = Phone = Email = Address = ErrorMessage = ""; NotifyState(); }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private async Task ReloadAsync()
    {
        if (!CanLeave) return;
        IsWorking = true; _ready = false; ErrorMessage = "";
        try
        {
            var profile = await _settingsService.GetInitialBusinessAsync();
            if (_departed) return;
            BusinessName = profile.Name; Phone = profile.Phone; Email = profile.Email; Address = profile.Address;
            _ready = true;
        }
        catch (Exception ex) { if (!_departed) ErrorMessage = "No se pudieron cargar los datos. Reintente o vuelva al acceso. " + ex.Message; }
        finally { IsWorking = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLeave))]
    private void GoBack() { if (CanLeave) _navigateToPreviousStep?.Invoke(); }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAndContinueAsync()
    {
        if (!CanEdit) return;
        IsWorking = true; ErrorMessage = "";
        try
        {
            await _settingsService.SaveInitialBusinessAsync(new(BusinessName, Phone, Email, Address));
            if (!_departed) _navigateToNextStep();
        }
        catch (Exception ex) { if (!_departed) ErrorMessage = "No se confirmó el guardado. Puede reintentar los mismos datos. " + ex.Message; }
        finally { IsWorking = false; }
    }
}
