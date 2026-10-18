using Microsoft.Extensions.DependencyInjection;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using System;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;

namespace Ticketfy.Core.Startup;

/// <summary>
/// Orchestrates the startup sequence: DB initialization, DI container setup,
/// and dependency-injection container setup.
/// </summary>
public static class AppBootstrapper
{
    public static async Task<IServiceProvider> BootstrapServicesAsync()
    {
        await DatabaseInitializer.InitializeAsync();

        var services = new ServiceCollection();
        services.AddSingleton<IFacturamaService>(_ =>
            LocalDevelopmentMode.FacturamaEnabled
                ? new FacturamaService(new System.Net.Http.HttpClient())
                : new UnavailableFacturamaService());

        return services.BuildServiceProvider();
    }
}
