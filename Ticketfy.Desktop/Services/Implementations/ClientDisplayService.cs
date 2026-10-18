using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Ticketfy.Core.State;
using Ticketfy.Views;
using Ticketfy.ViewModels;
using Serilog;
using System;

namespace Ticketfy.Services.Implementations;

public class ClientDisplayService
{
    private ClientDisplayWindow? _clientWindow;
    private readonly CartStateStore _cartStateStore;

    public ClientDisplayService(CartStateStore cartStateStore)
    {
        _cartStateStore = cartStateStore;
    }

    public void ShowClientDisplay()
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_clientWindow == null || !_clientWindow.IsVisible)
                {
                    // For resilience, check if there's actually a secondary screen
                    var desktopLifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                    var screensCount = desktopLifetime?.MainWindow?.Screens.ScreenCount ?? 1;

                    if (screensCount <= 1)
                    {
                        return; // No secondary display attached
                    }

                    _clientWindow = new ClientDisplayWindow();

                    var secondScreen = desktopLifetime?.MainWindow?.Screens.All.Count > 1
                                       ? desktopLifetime?.MainWindow?.Screens.All[1]
                                       : desktopLifetime?.MainWindow?.Screens.Primary;

                    if (secondScreen != null)
                    {
                        _clientWindow.Position = new Avalonia.PixelPoint(secondScreen.Bounds.X, secondScreen.Bounds.Y);
                        _clientWindow.WindowState = Avalonia.Controls.WindowState.FullScreen;
                    }

                    _clientWindow.Show();
                    _clientWindow.Closed += (s, e) => _clientWindow = null;
                }

                if (_clientWindow != null && _clientWindow.IsLoaded)
                {
                    _clientWindow.DataContext = new ClientDisplayViewModel(_cartStateStore);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Error al renderizar pantalla secundaria: {0}", ex);
        }
    }

    public void CloseClientDisplay()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_clientWindow != null)
            {
                _clientWindow.Close();
                _clientWindow = null;
            }
        });
    }
}
