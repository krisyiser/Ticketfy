using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Helpers;

namespace Ticketfy.Services.Implementations;

public class AutoUpdateService
{
    private const string ApiUrl = "https://valcore.cloud/api/latest-release";
    private const string BaseUrl = "https://valcore.cloud";

    public bool IsUpdateReadyToInstall { get; private set; }
    public string NewVersion { get; private set; } = string.Empty;
    public double DownloadProgress { get; private set; }
    private string? _downloadedInstallerPath;

    public event Action? UpdateAvailableEvent;
    public event Action<double>? DownloadProgressChangedEvent;
    public event Action? UpdateReadyToInstallEvent;
    public event Action<string>? UpdateFailedEvent;
    public event Action? UpdateUpToDateEvent;

    public AutoUpdateService()
    {
    }

    /// <summary>
    /// Checks for updates via valcore.cloud API/manifest with failsafe anti-HTML JSON verification.
    /// Downloads the update installer asynchronously with real-time percentage progress.
    /// </summary>
    public async Task CheckAndDownloadUpdatesAsync()
    {
        if (!LocalDevelopmentMode.RemoteInfrastructureEnabled)
        {
            Log.Information("Auto-update check skipped: local development mode is active");
            Dispatcher.UIThread.Post(() =>
                UpdateFailedEvent?.Invoke(LocalDevelopmentMode.RemoteInfrastructureUnavailableMessage));
            return;
        }

        try
        {
            Log.Information("AutoUpdateService: Checking for updates at {Url}", ApiUrl);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TicketfyDesktopApp/3.0");

            string? jsonString = null;

            // Try primary endpoint: /api/latest-release
            try
            {
                var resp1 = await httpClient.GetAsync($"{ApiUrl}?cb={DateTime.UtcNow.Ticks}");
                if (resp1.IsSuccessStatusCode)
                {
                    var raw1 = await resp1.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(raw1) && raw1.Trim().StartsWith("{"))
                    {
                        jsonString = raw1;
                    }
                }
            }
            catch (Exception ex1)
            {
                Log.Warning("AutoUpdateService: Endpoint {Url} query threw exception: {Msg}", ApiUrl, ex1.Message);
            }

            // Fallback to secondary endpoint: /downloads/releases.json
            if (string.IsNullOrEmpty(jsonString))
            {
                var fallbackUrl = $"{BaseUrl}/downloads/releases.json?cb={DateTime.UtcNow.Ticks}";
                Log.Information("AutoUpdateService: Attempting fallback update manifest at {Url}", fallbackUrl);
                try
                {
                    var resp2 = await httpClient.GetAsync(fallbackUrl);
                    if (resp2.IsSuccessStatusCode)
                    {
                        var raw2 = await resp2.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(raw2) && raw2.Trim().StartsWith("{"))
                        {
                            jsonString = raw2;
                        }
                    }
                }
                catch (Exception ex2)
                {
                    Log.Warning("AutoUpdateService: Fallback manifest {Url} query threw exception: {Msg}", fallbackUrl, ex2.Message);
                }
            }

            // Fallback 2 to GitHub raw manifest endpoint
            if (string.IsNullOrEmpty(jsonString))
            {
                var githubManifestUrl = $"https://raw.githubusercontent.com/krisyiser/NextVent/main/releases.json?cb={DateTime.UtcNow.Ticks}";
                Log.Information("AutoUpdateService: Attempting GitHub fallback update manifest at {Url}", githubManifestUrl);
                try
                {
                    var resp3 = await httpClient.GetAsync(githubManifestUrl);
                    if (resp3.IsSuccessStatusCode)
                    {
                        var raw3 = await resp3.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(raw3) && raw3.Trim().StartsWith("{"))
                        {
                            jsonString = raw3;
                        }
                    }
                }
                catch (Exception ex3)
                {
                    Log.Warning("AutoUpdateService: GitHub fallback manifest {Url} query threw exception: {Msg}", githubManifestUrl, ex3.Message);
                }
            }

            if (string.IsNullOrEmpty(jsonString))
            {
                Log.Warning("AutoUpdateService: No valid JSON release manifest obtained from server.");
                Dispatcher.UIThread.Post(() => UpdateUpToDateEvent?.Invoke());
                return;
            }

            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            string latestVersionStr = root.GetProperty("version").GetString() ?? string.Empty;
            Log.Information("AutoUpdateService: Server latest version is '{ServerVersion}', current local is '{CurrentVersion}'",
                latestVersionStr, AppVersionHelper.DisplayVersion);

            if (string.IsNullOrWhiteSpace(latestVersionStr))
            {
                Dispatcher.UIThread.Post(() => UpdateUpToDateEvent?.Invoke());
                return;
            }

            var serverVersionClean = latestVersionStr.Trim().TrimStart('v', 'V');
            var localVersionClean = AppVersionHelper.DisplayVersion.Trim().TrimStart('v', 'V');

            if (Version.TryParse(serverVersionClean, out var sVer) && Version.TryParse(localVersionClean, out var lVer))
            {
                if (sVer <= lVer)
                {
                    Log.Information("AutoUpdateService: Local version {Local} is up to date with server {Server}.", lVer, sVer);
                    Dispatcher.UIThread.Post(() => UpdateUpToDateEvent?.Invoke());
                    return;
                }
            }
            else
            {
                if (string.Equals(serverVersionClean, localVersionClean, StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.UIThread.Post(() => UpdateUpToDateEvent?.Invoke());
                    return;
                }
            }

            NewVersion = $"v{serverVersionClean}";
            Log.Information("AutoUpdateService: New version available: {NewVersion}. Preparing download...", NewVersion);

            Dispatcher.UIThread.Post(() =>
            {
                DownloadProgress = 0;
                UpdateAvailableEvent?.Invoke();
            });

            // Resolve download path
            string relativeDlPath = $"/downloads/Ticketfy-Setup-v{serverVersionClean}-x64.exe?v={serverVersionClean}";
            if (root.TryGetProperty("downloads", out var downloadsEl))
            {
                if (downloadsEl.TryGetProperty("exe", out var exeEl) && !string.IsNullOrEmpty(exeEl.GetString()))
                {
                    relativeDlPath = exeEl.GetString()!;
                }
                else if (downloadsEl.TryGetProperty("x64", out var x64El) && !string.IsNullOrEmpty(x64El.GetString()))
                {
                    relativeDlPath = x64El.GetString()!;
                }
            }

            string fullDownloadUrl = relativeDlPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? relativeDlPath
                : $"{BaseUrl}{(relativeDlPath.StartsWith("/") ? "" : "/")}{relativeDlPath}";

            string tempDir = Path.Combine(Path.GetTempPath(), "TicketfyUpdates");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string tempInstallerPath = Path.Combine(tempDir, $"Ticketfy-Setup-v{serverVersionClean}-x64.exe");

            Log.Information("AutoUpdateService: Downloading installer from {Url} to {Dest}", fullDownloadUrl, tempInstallerPath);

            using var response = await httpClient.GetAsync(fullDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(tempInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;
                if (totalBytes > 0)
                {
                    double progress = Math.Round((double)totalRead / totalBytes * 100.0, 1);
                    DownloadProgress = progress;
                    Dispatcher.UIThread.Post(() => DownloadProgressChangedEvent?.Invoke(progress));
                }
            }

            _downloadedInstallerPath = tempInstallerPath;
            IsUpdateReadyToInstall = true;
            Log.Information("AutoUpdateService: Update installer downloaded successfully to {Path}", tempInstallerPath);

            Dispatcher.UIThread.Post(() => UpdateReadyToInstallEvent?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoUpdateService: Failed during update check or download.");
            Dispatcher.UIThread.Post(() => UpdateFailedEvent?.Invoke(ex.Message));
        }
    }

    /// <summary>
    /// Applies the downloaded update installer silently and terminates current process.
    /// </summary>
    public void ApplyUpdatesAndRestart()
    {
        try
        {
            if (string.IsNullOrEmpty(_downloadedInstallerPath) || !File.Exists(_downloadedInstallerPath))
            {
                Log.Warning("AutoUpdateService: Downloaded installer path is invalid or file missing.");
                return;
            }

            Log.Information("AutoUpdateService: Executing silent update installer '{Path}' and terminating current process...", _downloadedInstallerPath);

            var psi = new ProcessStartInfo
            {
                FileName = _downloadedInstallerPath,
                Arguments = "/SILENT /NORESTART",
                UseShellExecute = true
            };

            Process.Start(psi);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoUpdateService: Failed to launch update installer.");
        }
    }

    public void StartPeriodicChecks(TimeSpan interval)
    {
        if (!LocalDevelopmentMode.RemoteInfrastructureEnabled)
        {
            Log.Debug("Periodic update checks not started: local development mode is active");
            return;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await CheckAndDownloadUpdatesAsync();
                await Task.Delay(interval);
            }
        });
    }
}
