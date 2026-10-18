using Ticketfy.Core.Configuration;
using Ticketfy.Core.Models;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public class LocalDevelopmentModeTests
{
    [Fact]
    public void RemoteIntegrations_ShouldRemainDisabled()
    {
        Assert.True(LocalDevelopmentMode.IsEnabled);
        Assert.False(LocalDevelopmentMode.RemoteInfrastructureEnabled);
        Assert.False(LocalDevelopmentMode.FacturamaEnabled);
    }

    [Fact]
    public async Task FacturamaStub_ShouldRejectRequestsWithoutNetworkAccess()
    {
        var service = new UnavailableFacturamaService();

        var exception = await Assert.ThrowsAsync<FacturamaUnavailableException>(
            () => service.CreateInvoiceAsync(new FacturamaCfdiRequest()));

        Assert.Equal(LocalDevelopmentMode.FacturamaUnavailableMessage, exception.Message);
    }

    [Fact]
    public async Task FacturamaProductionService_ShouldAlsoBeBlockedByLocalPolicy()
    {
        using var httpClient = new HttpClient();
        var service = new FacturamaService(httpClient);

        var exception = await Assert.ThrowsAsync<FacturamaUnavailableException>(
            () => service.CreateInvoiceAsync(new FacturamaCfdiRequest()));

        Assert.Equal(LocalDevelopmentMode.FacturamaUnavailableMessage, exception.Message);
    }
}
