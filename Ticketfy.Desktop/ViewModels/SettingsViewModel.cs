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
    private readonly Func<System.Collections.Generic.List<SettingsSectionItem>> _createSections;
    private long _usersLoadVersion;
    private long _sectionVersion;
    private readonly Func<string, Task>? _authorizeSection;
    private readonly System.Collections.Generic.HashSet<SettingsSectionItem> _loadedSections = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditSection))] private bool _isSectionLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditSection))] private bool _isSectionReady;
    public bool CanEditSection => IsSectionReady && !IsSectionLoading;

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
    public event Func<Task>? CloseRequested;

    public SettingsViewModel(
        IUserService userService,
        ISettingsService settingsService,
        IBackupService? backupService = null,
        IAuditService? auditService = null,
        Func<string>? auditActor = null,
        IDiagnosticExportService? diagnosticService = null,
        IOperationalTelemetry? operationalTelemetry = null,
        Ticketfy.Services.Implementations.RecoveryBundleService? recovery = null,
        Func<string, Task>? authorizeSection = null)
    {
        _userService = userService;
        _settingsService = settingsService;
        _authorizeSection = authorizeSection;

        _createSections = () => [
            new SettingsSectionItem("Personalización & UI", "Estilos, Colores & Fuentes", "PaletteOutline", new PersonalizacionSettingsViewModel(settingsService, loadOnCreate: false), true),
            new SettingsSectionItem("Identidad & Empresa", "Datos Comerciales & Fiscales", "Domain", new EmpresaSettingsViewModel(settingsService, loadOnCreate: false)),
            new SettingsSectionItem("Hardware & Tickets", "Impresoras 58/80mm & Lector", "PrinterOutline", new HardwareSettingsViewModel(settingsService, loadOnCreate: false)),
            new SettingsSectionItem("Sistema & Seguridad", "Base de Datos, PIN & Backup", "ShieldCheckOutline", new SeguridadSettingsViewModel(settingsService, backupService, diagnosticService, operationalTelemetry, recovery, loadOnCreate: false)),
            new SettingsSectionItem("Usuarios & Roles", "Control de Acceso RBAC", "AccountGroupOutline", new UsuariosSettingsViewModel(userService, settingsService, auditService, auditActor, loadOnCreate: false)),
            new SettingsSectionItem("Acerca de Ticketfy", "Licencia & Versión", "InformationOutline", new AcercaDeSettingsViewModel())
        ];
        Sections = new(_createSections());
        _currentSectionViewModel = Sections[0].ViewModel;
    }

    public void ClearSessionData()
    {
        ++_usersLoadVersion;
        ++_sectionVersion; _loadedSections.Clear(); IsSectionLoading = IsSectionReady = false;
        Users.Clear();
        NewUsername = NewFullName = NewPassword = NewPasswordHint = NewPin1 = NewPin2 = NewPin3 = NewPin4 = AdminDeletePassword = FeedbackMessage = string.Empty;
        UserToDelete = null; IsConfirmingAdminDelete = false;
        foreach (var section in Sections)
        {
            if (section.ViewModel is PersonalizacionSettingsViewModel visual) visual.DeactivateSession();
            if (section.ViewModel is UsuariosSettingsViewModel users) users.ClearSessionSecrets();
            if (section.ViewModel is SeguridadSettingsViewModel security) security.ClearSessionSecrets();
            if (section.ViewModel is HardwareSettingsViewModel hardware) hardware.DeactivateSession();
            if (section.ViewModel is EmpresaSettingsViewModel business) business.DeactivateSession();
        }
        Sections.Clear();
        foreach (var section in _createSections()) Sections.Add(section);
        CurrentSectionViewModel = Sections[0].ViewModel;
    }

    public SettingsViewModel() : this(null!, null!) { }

    public Task LoadCurrentSectionAsync() => LoadSectionAsync(Sections.First(s => ReferenceEquals(s.ViewModel, CurrentSectionViewModel)), refresh: true);

    [RelayCommand]
    private Task SelectSectionAsync(SettingsSectionItem targetSection) => LoadSectionAsync(targetSection, refresh: false);

    [RelayCommand]
    private Task RefreshSectionAsync() => LoadCurrentSectionAsync();

    private async Task LoadSectionAsync(SettingsSectionItem targetSection, bool refresh)
    {
        if (targetSection == null || !Sections.Contains(targetSection)) return;
        var version = ++_sectionVersion;
        if (refresh) _loadedSections.Remove(targetSection);
        IsSectionLoading = true; IsSectionReady = false; FeedbackMessage = "";
        string permission = targetSection.ViewModel switch
        {
            HardwareSettingsViewModel => Ticketfy.Core.Security.PermissionCatalog.SettingsHardware,
            SeguridadSettingsViewModel => Ticketfy.Core.Security.PermissionCatalog.SettingsSecurity,
            UsuariosSettingsViewModel => Ticketfy.Core.Security.PermissionCatalog.UsersManage,
            _ => Ticketfy.Core.Security.PermissionCatalog.SettingsBusiness
        };
        try
        {
            if (_authorizeSection is not null) await _authorizeSection(permission);
            if (version != _sectionVersion) return;
            if (refresh || !_loadedSections.Contains(targetSection))
            {
                switch (targetSection.ViewModel)
                {
                    case PersonalizacionSettingsViewModel p: await p.LoadAsync(); if (!p.HasLoadedData) throw new InvalidOperationException(); break;
                    case EmpresaSettingsViewModel e: await e.LoadAsync(); if (!e.HasLoadedData) throw new InvalidOperationException(); break;
                    case HardwareSettingsViewModel h: await h.LoadAsync(); if (!h.HasLoadedData) throw new InvalidOperationException(); break;
                    case SeguridadSettingsViewModel s: await s.LoadAsync(); if (!s.HasLoadedData) throw new InvalidOperationException(); break;
                    case UsuariosSettingsViewModel u: await u.InitAsync(); break;
                }
            }
            if (version != _sectionVersion) return;
            if (_authorizeSection is not null) await _authorizeSection(permission);
            if (version != _sectionVersion) return;
            _loadedSections.Add(targetSection);
            foreach (var sec in Sections) sec.IsSelected = sec == targetSection;
            CurrentSectionViewModel = targetSection.ViewModel;
            IsSectionReady = true;
        }
        catch (Exception)
        {
            if (version == _sectionVersion) FeedbackMessage = "No se pudo cargar la sección. Revise sus permisos y vuelva a seleccionarla o actualice.";
        }
        finally { if (version == _sectionVersion) IsSectionLoading = false; }
    }

    [RelayCommand] private Task CloseAsync() => CloseRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task SaveAllSettingsAsync()
    {
        if (!CanEditSection) { FeedbackMessage = "Cargue una sección autorizada antes de guardar."; return; }
        var version = _sectionVersion;
        bool saved = CurrentSectionViewModel switch
        {
            PersonalizacionSettingsViewModel p => await p.SaveAsync(),
            EmpresaSettingsViewModel e => await e.SaveAsync(),
            HardwareSettingsViewModel h => await h.SaveAsync(),
            SeguridadSettingsViewModel s => await s.SaveAsync(),
            _ => false
        };
        if (version != _sectionVersion) return;
        if (!saved) { FeedbackMessage = "No se confirmó el guardado. Revise el mensaje de la sección seleccionada."; return; }
        FeedbackMessage = "¡Configuración guardada correctamente!";
        if (SettingsSaved != null) await SettingsSaved.Invoke();
    }
}
