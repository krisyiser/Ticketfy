using Avalonia;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const int SW_RESTORE = 9;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--verify-startup", StringComparer.Ordinal))
        {
            try { Core.Startup.StartupVerification.Run(args); }
            catch (Exception ex) { Console.Error.WriteLine("Startup verification failed: " + ex.GetType().Name); Environment.ExitCode = 1; }
            return;
        }
        try
        {
            int dataDirectoryArgument = Array.IndexOf(args, "--data-directory");
            if (dataDirectoryArgument >= 0)
            {
                if (dataDirectoryArgument + 1 >= args.Length) throw new ArgumentException("Falta la carpeta después de --data-directory.");
                if (AppDataPaths.IsInternalTesting)
                    InternalTestingProfile.ValidateDirectory(args[dataDirectoryArgument + 1],
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                AppDataPaths.UseDataDirectory(args[dataDirectoryArgument + 1]);
            }
            // Inno Setup detects this presence mutex and refuses to update/uninstall a running test edition.
            // It never closes an operating POS or shares its installation identity.
            using var testEditionPresence = AppDataPaths.IsInternalTesting ? new System.Threading.Mutex(false, InternalTestingProfile.InstallerMutex) : null;
            VelopackApp.Build().Run();
            using var ownership = new Ticketfy.Core.Startup.ApplicationInstanceLock(AppDataPaths.RootDirectory);
            if (!ownership.IsAcquired)
            {
                ActivateExistingWindow();
                return;
            }
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
            Environment.ExitCode = 1;
            if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero,
                "No se pudo iniciar Ticketfy. Revise la ruta de datos, los permisos y los registros de diagnóstico. No borre bases ni llaves. Si perdió el perfil, utilice el asistente --recovery con su paquete integral.\n\nCódigo: " + ex.GetType().Name,
                "Ticketfy — Inicio detenido", 0x10);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ActivateExistingWindow()
    {
        foreach (var process in Process.GetProcessesByName("Ticketfy.Desktop"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(process.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
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
