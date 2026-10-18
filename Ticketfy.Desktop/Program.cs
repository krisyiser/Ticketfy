using Avalonia;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Velopack;
using Ticketfy.Core.Configuration;
using Ticketfy.Logging;

namespace Ticketfy;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private const int SW_RESTORE = 9;

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Initialize Velopack (crucial for shortcuts, uninstalls, and OTA background updates).
        // TLS certificate validation intentionally uses the operating-system trust store.
        VelopackApp.Build().Run();

        // 2. Ensure single responsive instance: kill ghost/hung processes (0 UI window) or bring active UI window to front
        if (!EnsureSingleResponsiveInstance())
        {
            return;
        }

        try
        {
            var logDir = AppDataPaths.LogDirectory;
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    new SanitizingTextFormatter(),
                    Path.Combine(logDir, "ticketfy_.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .CreateLogger();

            RunAvaloniaApp(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "TICKETFY! crashed fatally during initialization");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int GetParentProcessId(IntPtr handle)
    {
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
            if (status == 0)
            {
                return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
        }
        catch { }
        return -1;
    }

    private static bool EnsureSingleResponsiveInstance()
    {
        try
        {
            var currentProc = Process.GetCurrentProcess();

            // NEVER enforce process killing if running from Setup, Velopack, or installer executables
            if (currentProc.ProcessName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                currentProc.ProcessName.Contains("vpk", StringComparison.OrdinalIgnoreCase) ||
                currentProc.ProcessName.Contains("Update", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int parentPid = GetParentProcessId(currentProc.Handle);

            // ONLY target processes named strictly "Ticketfy.Desktop"
            var existingProcesses = Process.GetProcessesByName("Ticketfy.Desktop");

            foreach (var p in existingProcesses)
            {
                // Skip self and parent process
                if (p.Id == currentProc.Id || (parentPid > 0 && p.Id == parentPid)) continue;

                try
                {
                    string pName = p.ProcessName;
                    if (pName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                        pName.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                        pName.Contains("vpk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Check if existing process has an active UI main window
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        return false; // Exit current duplicate instance cleanly
                    }
                    else
                    {
                        // Ghost process without UI window -> kill it so new instance can run
                        p.Kill();
                    }
                }
                catch
                {
                    // Ignore permission / already exited exceptions
                }
            }
        }
        catch
        {
            // Fallback: continue startup
        }
        return true;
    }

    private static void RunAvaloniaApp(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            if (ex.GetType().Name != "TaskCanceledException")
                Log.Fatal(ex, "TICKETFY! Avalonia runtime crashed");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
