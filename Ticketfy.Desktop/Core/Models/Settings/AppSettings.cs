using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Text.Json.Serialization;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Operations;

namespace Ticketfy.Core.Models.Settings;

public enum ThemeMode
{
    Light,
    Dark,
    System,
    HighContrast,
    Cyberpunk,
    Emerald,
    Nordic,
    Amber
}

public enum UIDensity
{
    Compact,
    Comfortable,
    Touch
}

public enum PaperWidth
{
    Paper58mm,
    Paper80mm,
    PaperLetter
}

/// <summary>
/// Unified Root Settings POCO representing the entire application configuration and UI customization state.
/// Designed under Protocol Valcore v4.0 for zero-fragmentation reactive state management.
/// </summary>
public sealed class AppSettings
{
    public VisualCustomizationConfig Visual { get; set; } = new();
    public CompanyProfileConfig Company { get; set; } = new();
    public TicketLayoutConfig Ticket { get; set; } = new();
    public HardwareConfig Hardware { get; set; } = new();
    public SystemSecurityConfig System { get; set; } = new();
}

/// <summary>
/// Reactive Visual Customization Configuration backing the live UI theme engine.
/// Inherits from ObservableObject so slider, toggle, and combobox mutations dispatch 0ms GPU updates.
/// </summary>
public partial class VisualCustomizationConfig : ObservableObject
{
    [ObservableProperty] private ThemeMode _mode = ThemeMode.Light;
    [ObservableProperty] private string _themeName = "Modo Claro";
    [ObservableProperty] private string _primaryColor = "#3B82F6";
    [ObservableProperty] private string _accentColor = "#38BDF8";
    [ObservableProperty] private string _successColor = "#10B981";
    [ObservableProperty] private string _dangerColor = "#EF4444";
    [ObservableProperty] private string _sidebarBgColor = "#F1F5F9";
    [ObservableProperty] private double _cornerRadius = 8.0;
    [ObservableProperty] private double _fontSizeScale = 14.0;
    [ObservableProperty] private double _posPriceFontSize = 24.0;
    [ObservableProperty] private string _fontFamily = "Inter";
    [ObservableProperty] private bool _enableAnimations = true;
    [ObservableProperty] private double _transitionDurationMs = 120.0;
    [ObservableProperty] private UIDensity _density = UIDensity.Comfortable;
    [ObservableProperty] private double _glassmorphismBlur = 0.0;
    [ObservableProperty] private double _glassmorphismOpacity = 100.0;
    [ObservableProperty] private double _posCartWidth = 380.0;
    [ObservableProperty] private string _sidebarPosition = "Izquierda";
    [ObservableProperty] private string _cartPosition = "Derecha";
    [ObservableProperty] private bool _showStockBadge = true;
    [ObservableProperty] private bool _showSkuProducto = true;
    [ObservableProperty] private bool _showQuickAddButton = true;
    [ObservableProperty] private bool _showProductImages = true;
    [ObservableProperty] private double _grosorBordePx = 1.0;
    [ObservableProperty] private double _escalaLogoTopbar = 24.0;
}

public sealed class CompanyProfileConfig
{
    public string CommercialName { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public string FiscalRegime { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
}

public sealed class TicketLayoutConfig
{
    public string HeaderText { get; set; } = "¡Gracias por su compra!";
    public string FooterText { get; set; } = "Conserve este ticket para cualquier aclaración.";
    public string PaperWidthMm { get; set; } = "80mm";
    public bool AutoPrintReceipt { get; set; } = true;
    public bool ShowTaxBreakdown { get; set; } = true;
    public bool PrintLogo { get; set; } = true;
}

public sealed class HardwareConfig
{
    public string PrinterPort { get; set; } = "ImpresoraTickets";
    public string ScannerPort { get; set; } = "HID Barcode Scanner";
    public string ScalePort { get; set; } = "COM1";
    public bool EnableCashDrawerTrigger { get; set; } = true;
    public int CashDrawerPin { get; set; }
}

public sealed class SystemSecurityConfig
{
    public bool EnablePinLock { get; set; } = true;
    public int AutoLockTimeoutMinutes { get; set; } = 15;
    public string BackupPath { get; set; } = AppDataPaths.BackupDirectory;
    public bool AutoBackupOnClose { get; set; } = true;
    public bool AutomaticBackupsEnabled { get; set; } = true;
    public int BackupIntervalHours { get; set; } = 24;
    public int BackupRetentionCount { get; set; } = 30;
    public int BackupRetentionDays { get; set; } = 30;
    public bool VerifyAutomaticBackups { get; set; } = true;
    public TelemetryConsent TelemetryConsent { get; set; } = TelemetryConsent.Disabled;
    public bool EnableAuditLogs { get; set; } = true;
}
