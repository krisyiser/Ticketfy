using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Management;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Services;
using Ticketfy.Services.Interfaces;
using Serilog;
using Ticketfy.Core.Configuration;

namespace Ticketfy.Services.Implementations;

public class BusinessProfile
{
    [JsonPropertyName("businessName")] public string BusinessName { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
}

public class BusinessData
{
    [JsonPropertyName("commercialName")] public string CommercialName { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("industry")] public string Industry { get; set; } = string.Empty;
    [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;
    [JsonPropertyName("city")] public string City { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
}

public class SystemSpecs
{
    [JsonPropertyName("cpuModel")] public string CpuModel { get; set; } = string.Empty;
    [JsonPropertyName("totalRam")] public string TotalRam { get; set; } = string.Empty;
    [JsonPropertyName("primaryStorage")] public string PrimaryStorage { get; set; } = string.Empty;
}

public class SessionInfo
{
    [JsonPropertyName("activeUser")] public string ActiveUser { get; set; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
}

public class ProvisionPayload
{
    [JsonPropertyName("hardwareId")] public string HardwareId { get; set; } = string.Empty;
    [JsonPropertyName("localIp")] public string LocalIp { get; set; } = string.Empty;
    [JsonPropertyName("installedVersion")] public string InstalledVersion { get; set; } = string.Empty;
    [JsonPropertyName("business")] public BusinessData Business { get; set; } = new();
    [JsonPropertyName("system")] public SystemSpecs System { get; set; } = new();
    [JsonPropertyName("session")] public SessionInfo Session { get; set; } = new();
}

public class ProvisionResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("nodeId")]
    public string? NodeId { get; set; }
    [JsonPropertyName("licenseToken")]
    public string? LicenseToken { get; set; }
}

[JsonSerializable(typeof(ProvisionPayload))]
[JsonSerializable(typeof(ProvisionResponse))]
public partial class TelemetryJsonContext : JsonSerializerContext { }

public class DeviceRegistrationService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService? _settingsService;
    private readonly ISessionManager? _sessionManager;
    private const string API_HOST = "api.valcore.cloud";
    private const string API_URL = "https://api.valcore.cloud/api/v1/nodes/provision";

    public DeviceRegistrationService(ISettingsService? settingsService = null, ISessionManager? sessionManager = null)
    {
        _settingsService = settingsService;
        _sessionManager = sessionManager;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"TicketfyDesktopClient/{Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion.TrimStart('v', 'V')} (Windows; ValcoreEngine)");
    }

    public async Task RegisterNodeAsync(BusinessProfile profile)
    {
        await PingServerAsync(profile);
    }

    public async Task PingServerAsync(BusinessProfile profile)
    {
        if (!LocalDevelopmentMode.RemoteInfrastructureEnabled)
        {
            Log.Debug("Device registration skipped: local development mode is active");
            return;
        }

        try
        {
            string? apiKey = Environment.GetEnvironmentVariable("TICKETFY_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("Device registration skipped because TICKETFY_API_KEY is not configured");
                return;
            }

            var machineId = HardwareIdentityHelper.GetMotherboardUUID();
            if (string.IsNullOrWhiteSpace(machineId))
            {
                Log.Warning("Device registration skipped because no stable hardware identifier is available");
                return;
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var localIp = NetworkHelper.GetLocalIpAddress() ?? "127.0.0.1";

            var payload = new ProvisionPayload
            {
                HardwareId = machineId,
                LocalIp = localIp,
                InstalledVersion = Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion
            };

            // 1. Lectura de Hardware Profunda (AOT-Safe)
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var cpuName = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
                    payload.System.CpuModel = string.IsNullOrWhiteSpace(cpuName) ? "Unknown CPU" : cpuName.Trim();
                }
                else
                {
                    payload.System.CpuModel = "Unknown CPU";
                }

                // RAM (AOT-Safe)
                var memInfo = GC.GetGCMemoryInfo();
                payload.System.TotalRam = $"{(memInfo.TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024)):F1} GB";

                // Storage (AOT-Safe)
                var mainDrive = System.IO.DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed);
                if (mainDrive != null)
                {
                    payload.System.PrimaryStorage = $"Disco Local - {(mainDrive.TotalSize / (1024.0 * 1024 * 1024)):F0} GB";
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read hardware specs");
            }

            // 2. Lectura de Entorno de Negocio
            if (_settingsService != null)
            {
                var name = await _settingsService.GetAsync("EmpresaNombreComercial");
                if (string.IsNullOrWhiteSpace(name)) name = await _settingsService.GetAsync("BusinessName");
                payload.Business.CommercialName = string.IsNullOrWhiteSpace(name) ? profile.BusinessName : name;
                if (string.IsNullOrWhiteSpace(payload.Business.CommercialName)) payload.Business.CommercialName = "Negocio Ticketfy";

                var email = await _settingsService.GetAsync("EmpresaEmailContacto");
                if (string.IsNullOrWhiteSpace(email)) email = await _settingsService.GetAsync("BusinessEmail");
                payload.Business.Email = string.IsNullOrWhiteSpace(email) ? profile.Email : email;

                var industry = await _settingsService.GetAsync("EmpresaGiroComercial");
                payload.Business.Industry = string.IsNullOrWhiteSpace(industry) ? "Comercio General" : industry;

                var calle = await _settingsService.GetAsync("EmpresaCalleYNumero") ?? await _settingsService.GetAsync("BusinessAddress");
                var colonia = await _settingsService.GetAsync("EmpresaColonia");
                var ciudad = await _settingsService.GetAsync("EmpresaCiudadMunicipio");
                var estado = await _settingsService.GetAsync("EmpresaEstado");

                var locParts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(calle)) locParts.Add(calle.Trim());
                if (!string.IsNullOrWhiteSpace(colonia)) locParts.Add(colonia.Trim());
                if (!string.IsNullOrWhiteSpace(ciudad)) locParts.Add(ciudad.Trim());
                if (!string.IsNullOrWhiteSpace(estado)) locParts.Add(estado.Trim());

                payload.Business.Location = locParts.Count > 0 ? string.Join(", ", locParts) : string.Empty;
                payload.Business.City = !string.IsNullOrWhiteSpace(ciudad) ? ciudad.Trim() : string.Empty;
                payload.Business.State = !string.IsNullOrWhiteSpace(estado) ? estado.Trim() : string.Empty;
            }
            else
            {
                payload.Business.CommercialName = string.IsNullOrWhiteSpace(profile.BusinessName) ? "Negocio Ticketfy" : profile.BusinessName;
            }

            // 3. Lectura de Sesión Activa
            if (_sessionManager?.CurrentCashier != null)
            {
                payload.Session.ActiveUser = _sessionManager.CurrentCashier.FullName ?? _sessionManager.CurrentCashier.Username ?? "Unknown";
                payload.Session.Role = _sessionManager.CurrentCashier.Role.ToString();
            }

            // Fire and Forget
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Aumentado a 15s para redes lentas

                    var request = new HttpRequestMessage(HttpMethod.Post, API_URL);
                    request.Headers.Host = API_HOST;
                    request.Content = System.Net.Http.Json.JsonContent.Create(payload, TelemetryJsonContext.Default.ProvisionPayload);

                    var response = await _httpClient.SendAsync(request, cts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorText = await response.Content.ReadAsStringAsync(cts.Token);
                        Log.Warning($"Fallo en Phone Home. Status: {response.StatusCode}. Detalle: {errorText}");

                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            var licensePath = AppDataPaths.LicenseFile;

                            if (System.IO.File.Exists(licensePath))
                            {
                                await System.IO.File.WriteAllTextAsync(licensePath, "REVOKED", cts.Token);
                                Log.Warning("❌ LICENCIA REVOCADA POR EL SERVIDOR (Kill Switch Activo).");
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    if (App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                                    {
                                        if (desktop.MainWindow != null)
                                        {
                                            desktop.MainWindow.Content = new Ticketfy.Views.LicenseLockedView { DataContext = new Ticketfy.ViewModels.LicenseLockedViewModel() };
                                        }
                                    }
                                });
                            }
                        }
                    }
                    else
                    {
                        Log.Information("✅ TELEMETRÍA ENVIADA CON ÉXITO AL TICKETFY HUB.");
                        try
                        {
                            var jsonResponse = await response.Content.ReadFromJsonAsync(TelemetryJsonContext.Default.ProvisionResponse, cts.Token);
                            if (jsonResponse != null)
                            {
                                if (!string.IsNullOrEmpty(jsonResponse.LicenseToken))
                                {
                                    AppDataPaths.EnsureRequiredDirectories();
                                    var licensePath = AppDataPaths.LicenseFile;
                                    await System.IO.File.WriteAllTextAsync(licensePath, jsonResponse.LicenseToken, cts.Token);
                                    Log.Information("✅ LICENCIA ACTUALIZADA DESDE TICKETFY HUB.");
                                }
                                else
                                {
                                    var licensePath = AppDataPaths.LicenseFile;

                                    if (System.IO.File.Exists(licensePath))
                                    {
                                        await System.IO.File.WriteAllTextAsync(licensePath, "REVOKED", cts.Token);
                                        Log.Warning("❌ LICENCIA REVOCADA POR EL SERVIDOR (Kill Switch Activo).");
                                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                        {
                                            if (App.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                                            {
                                                if (desktop.MainWindow != null && !(desktop.MainWindow.Content is Ticketfy.Views.LicenseLockedView))
                                                {
                                                    desktop.MainWindow.Content = new Ticketfy.Views.LicenseLockedView { DataContext = new Ticketfy.ViewModels.LicenseLockedViewModel() };
                                                }
                                            }
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception innerEx)
                        {
                            Log.Error(innerEx, "Error al procesar la respuesta de la licencia.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Error crítico en Phone Home: {ex.Message}", ex);
                }
            });

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error en inicialización de Phone Home");
        }
    }

    private System.Threading.Timer? _heartbeatTimer;

    /// <summary>
    /// Inicia el envío periódico de telemetría en segundo plano (Heartbeat cada 2 minutos).
    /// </summary>
    public void StartPeriodicHeartbeat()
    {
        if (!LocalDevelopmentMode.RemoteInfrastructureEnabled)
        {
            Log.Debug("Device heartbeat not started: local development mode is active");
            return;
        }

        if (_heartbeatTimer != null) return;

        _heartbeatTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                await PingServerAsync(new BusinessProfile());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error en heartbeat periódico de telemetría.");
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
    }
}

