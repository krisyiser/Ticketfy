using Ticketfy.Core.Startup;
using Xunit;

namespace Ticketfy.Desktop.Tests;

public sealed class EnterpriseStartupTests
{
    [Fact]
    public void InstanceLock_RefusesSecondOwnerWithoutKillingItAndReleasesCleanly()
    {
        string path = Path.Combine(Path.GetTempPath(), "ticketfy-lock-test-" + Guid.NewGuid().ToString("N"));
        using (var first = new ApplicationInstanceLock(path))
        {
            Assert.True(first.IsAcquired);
            bool? acquired = null;
            var peer = new Thread(() => { using var second = new ApplicationInstanceLock(path); acquired = second.IsAcquired; });
            peer.Start();
            Assert.True(peer.Join(TimeSpan.FromSeconds(5)));
            Assert.False(acquired);
            using var separate = new ApplicationInstanceLock(path + "-isolated");
            Assert.True(separate.IsAcquired);
        }
        using var later = new ApplicationInstanceLock(path);
        Assert.True(later.IsAcquired);
        Assert.False(Directory.Exists(path));
    }
}
