using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ticketfy.ViewModels;
using System;

namespace Ticketfy.Views;

/// <summary>
/// MainWindow code-behind. Handles global function key shortcuts (F2-F10) for zero-mouse operation,
/// fullscreen toggling, and global idle timeout auto-lock.
/// </summary>
public partial class MainWindow : Window
{
    private DispatcherTimer _idleTimer;
    private int _idleTimeoutMinutes = 5;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => (DataContext as MainWindowViewModel)?.Hardware.Start();

        _idleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(_idleTimeoutMinutes)
        };
        _idleTimer.Tick += OnIdleTimeout;
        _idleTimer.Start();
        Closed += (_, _) => { _idleTimer.Stop(); (DataContext as MainWindowViewModel)?.Hardware.Dispose(); };

        // Global Event Listeners for Inactivity auto-lock
        AddHandler(PointerMovedEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnUserActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnUserActivity, RoutingStrategies.Tunnel);
    }

    private async void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ToggleFullscreenRequested += ToggleFullscreen;

            // Load idle timeout dynamically from VM settings
            int minutes = await vm.GetIdleTimeoutMinutesAsync();
            _idleTimeoutMinutes = minutes;
            _idleTimer.Interval = TimeSpan.FromMinutes(_idleTimeoutMinutes);
            _idleTimer.Stop();
            _idleTimer.Start();
        }
    }

    private void ToggleFullscreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnLockClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.TriggerAutoLock();
        }
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnUserActivity(object? sender, RoutedEventArgs e)
    {
        // Reset timer on any mouse/keyboard activity
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private async void OnIdleTimeout(object? sender, EventArgs e)
    {
        _idleTimer.Stop(); // Pause timer while evaluating

        if (DataContext is MainWindowViewModel vm)
        {
            // Only trigger lock if we are NOT on a login/setup screen and NOT already locked
            bool isSafeToLock = vm.ActiveViewModel is not LoginViewModel
                             && vm.ActiveViewModel is not FirstTimeSetupViewModel
                             && vm.ActiveViewModel is not WelcomeLicenseViewModel
                             && !vm.IsLocked;

            if (isSafeToLock)
            {
                vm.TriggerAutoLock();
            }
            await Task.CompletedTask;
        }

        // Restart timer only if we didn't just lock the screen
        if (DataContext is MainWindowViewModel vmRestart && !vmRestart.IsLocked)
        {
            _idleTimer.Start();
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.IsDialogOverlayOpen && vm.ActiveDialogViewModel is Ticketfy.ViewModels.Dialogs.OpenShiftDialogViewModel openShiftVm)
                {
                    openShiftVm.CancelCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.F2:
                vm.NavigateToCommand.Execute("pos");
                e.Handled = true;
                break;
            case Key.F3:
                vm.NavigateToCommand.Execute("inventory");
                e.Handled = true;
                break;
            case Key.F4:
                vm.NavigateToCommand.Execute("customers");
                e.Handled = true;
                break;
            case Key.F5:
                vm.NavigateToCommand.Execute("suppliers");
                e.Handled = true;
                break;
            case Key.F6:
                vm.NavigateToCommand.Execute("expenses");
                e.Handled = true;
                break;
            case Key.F7:
                vm.NavigateToCommand.Execute("history");
                e.Handled = true;
                break;
            case Key.F8:
                vm.NavigateToCommand.Execute("promotions");
                e.Handled = true;
                break;
            case Key.F9:
                vm.NavigateToCommand.Execute("fiscal");
                e.Handled = true;
                break;
            case Key.F10:
                vm.NavigateToCommand.Execute("settings");
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
        }
    }
}
