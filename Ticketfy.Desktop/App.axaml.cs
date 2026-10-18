using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ticketfy.Views;
using Ticketfy.ViewModels;
using Serilog;
using System;
using Ticketfy.Core.Configuration;
using Ticketfy.Logging;

namespace Ticketfy;

public partial class App : Application
{
    public new static App? Current => Application.Current as App;
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SetupGlobalExceptionHandling();
    }

    private void SetupGlobalExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogFatalError(e.ExceptionObject as Exception, "AppDomain Unhandled");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogFatalError(e.Exception, "TaskScheduler Unobserved");
            e.SetObserved();
        };
    }

    private void LogFatalError(Exception? ex, string source)
    {
        if (ex == null) return;

        try
        {
            string logDirectory = AppDataPaths.LogDirectory;

            if (!System.IO.Directory.Exists(logDirectory))
            {
                System.IO.Directory.CreateDirectory(logDirectory);
            }

            string logFilePath = System.IO.Path.Combine(logDirectory, "crash_log.txt");
            string errorBlock = $"--- CRASH REPORT: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n" +
                                $"Source: {LogSanitizer.Sanitize(source)}\n" +
                                $"Message: {LogSanitizer.Sanitize(ex.Message)}\n" +
                                $"StackTrace: {LogSanitizer.Sanitize(ex.StackTrace)}\n\n";

            System.IO.File.AppendAllText(logFilePath, errorBlock);

            Log.Fatal(ex, "Fatal crash caught by black box logger from source {Source}", source);
        }
        catch
        {
            // Fail silently
        }
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Information("TICKETFY! {Version} — Avalonia Native Desktop starting", Ticketfy.Core.Helpers.AppVersionHelper.DisplayVersion);

            // Show splash window immediately so user gets instant visual response
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            try
            {
                this.Services = await Ticketfy.Core.Startup.AppBootstrapper.BootstrapServicesAsync();
                desktop.Exit += (_, _) => (Services as IDisposable)?.Dispose();
                var licenseService = new Ticketfy.Services.Security.LicenseEnforcementService();
                if (licenseService.IsSystemLocked())
                {
                    Log.Warning("System locked: Kill Switch activated due to invalid or missing license.jwt.");
                    var lockWindow = new Avalonia.Controls.Window
                    {
                        Content = new Ticketfy.Views.LicenseLockedView { DataContext = new Ticketfy.ViewModels.LicenseLockedViewModel() },
                        SystemDecorations = Avalonia.Controls.SystemDecorations.None,
                        WindowState = Avalonia.Controls.WindowState.Maximized,
                        Topmost = true
                    };
                    desktop.MainWindow = lockWindow;
                    lockWindow.Show();
                    splash.Close();
                }
                else
                {
                    Log.Information("License validated successfully. Starting normal operation...");
                    var mainWin = new MainWindow
                    {
                        DataContext = new MainWindowViewModel()
                    };

                    desktop.MainWindow = mainWin;
                    mainWin.Show();
                    splash.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to initialize application services during boot");
                LogFatalError(ex, "AppBootstrapper Failed");

                var errorWindow = new Avalonia.Controls.Window
                {
                    Title = "TICKETFY! — Error de Inicio",
                    Width = 550,
                    Height = 320,
                    WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
                    Content = new Avalonia.Controls.TextBlock
                    {
                        Text = $"Error crítico al inicializar la base de datos o servicios de TICKETFY!:\n\n{ex.Message}\n\nDetalles registrados en %LocalAppData%\\ticketfy\\Logs\\crash_log.txt",
                        Margin = new Avalonia.Thickness(25),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 14
                    }
                };
                desktop.MainWindow = errorWindow;
                errorWindow.Show();
                splash.Close();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
