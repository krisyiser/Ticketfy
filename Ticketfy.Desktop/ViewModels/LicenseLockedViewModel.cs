using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Core.Configuration;

namespace Ticketfy.ViewModels;

public partial class LicenseLockedViewModel : ObservableObject
{
    [ObservableProperty]
    private string _lockMessage = "Suscripción Expirada. Contacte a Soporte";

    [ObservableProperty]
    private bool _isChecking = false;

    [RelayCommand]
    private Task RevalidateLicenseAsync()
    {
        IsChecking = false;
        LockMessage = $"{LocalDevelopmentMode.RemoteInfrastructureUnavailableMessage} La licencia debe validarse localmente.";
        return Task.CompletedTask;
    }
}
