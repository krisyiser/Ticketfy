using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Services;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Settings;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public record ColorPaletteItem(string Name, string HexColor);
public record FontSizeScaleOption(string Name, string SizePx, string Description);
public record KeyboardShortcutItem(string Shortcut, string ActionName, string Category);

/// <summary>
/// Master Settings Coordinator ViewModel under Protocol Valcore v4.0.
/// Manages atomic section switching via CurrentSectionViewModel to eliminate Z-index visual leaks.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService = ThemeService.Instance;
    private readonly IUserService? _userService;
    private readonly ISettingsService? _settingsService;

    // App Meta
    public string AppVersion => Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion;
    public string CurrentAppVersion => Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion;
    public string FullAppVersionTitle => Ticketfy.Core.Helpers.AppVersionHelper.FullTitle;

    // Observable Sections List
    public ObservableCollection<SettingsSectionItem> Sections { get; } = [];

    // Active Section ViewModel (Bound to TransitioningContentControl)
    [ObservableProperty] private ObservableObject _currentSectionViewModel;

    // Status Notification Message
    [ObservableProperty] private string _feedbackMessage = string.Empty;

    public event Func<Task>? SettingsSaved;

    public SettingsViewModel(IUserService userService, ISettingsService settingsService, IBackupService? backupService = null, IAuditService? auditService = null, Func<string>? auditActor = null)
    {
        _userService = userService;
        _settingsService = settingsService;

        var personalizacionVM = new PersonalizacionSettingsViewModel(settingsService);
        var empresaVM = new EmpresaSettingsViewModel(settingsService);
        var hardwareVM = new HardwareSettingsViewModel(settingsService);
        var seguridadVM = new SeguridadSettingsViewModel(settingsService, backupService);
        var usuariosVM = new UsuariosSettingsViewModel(userService, settingsService, auditService, auditActor);
        var acercaDeVM = new AcercaDeSettingsViewModel();

        Sections = [
            new SettingsSectionItem("Personalización & UI", "Estilos, Colores & Fuentes", "PaletteOutline", personalizacionVM, true),
            new SettingsSectionItem("Identidad & Empresa", "Datos Comerciales & Fiscales", "Domain", empresaVM),
            new SettingsSectionItem("Hardware & Tickets", "Impresoras 58/80mm & Lector", "PrinterOutline", hardwareVM),
            new SettingsSectionItem("Sistema & Seguridad", "Base de Datos, PIN & Backup", "ShieldCheckOutline", seguridadVM),
            new SettingsSectionItem("Usuarios & Roles", "Control de Acceso RBAC", "AccountGroupOutline", usuariosVM),
            new SettingsSectionItem("Acerca de Ticketfy", "Licencia & Versión", "InformationOutline", acercaDeVM)
        ];

        _currentSectionViewModel = personalizacionVM;
    }

    public SettingsViewModel() : this(null!, null!) { }

    [RelayCommand]
    private void SelectSection(SettingsSectionItem targetSection)
    {
        if (targetSection == null) return;
        foreach (var sec in Sections)
        {
            sec.IsSelected = (sec == targetSection);
        }
        CurrentSectionViewModel = targetSection.ViewModel;
    }

    [RelayCommand] private void Close() { }

    [RelayCommand]
    private async Task SaveAllSettingsAsync()
    {
        if (CurrentSectionViewModel is PersonalizacionSettingsViewModel p) await p.SaveAsync();
        else if (CurrentSectionViewModel is EmpresaSettingsViewModel e) await e.SaveAsync();
        else if (CurrentSectionViewModel is HardwareSettingsViewModel h) await h.SaveAsync();
        else if (CurrentSectionViewModel is SeguridadSettingsViewModel s) await s.SaveAsync();

        FeedbackMessage = "¡Configuración guardada correctamente!";
        if (SettingsSaved != null) await SettingsSaved.Invoke();
    }
}
