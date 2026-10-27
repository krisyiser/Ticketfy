using System.Security.Cryptography;
using System.Text;

namespace Ticketfy.Core.Startup;

/// <summary>Exclusive process ownership without terminating another instance or an in-progress checkout.</summary>
internal sealed class ApplicationInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsAcquired { get; }
    public ApplicationInstanceLock(string dataDirectory)
    {
        string canonical = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        _mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Local\\" : "") + "Ticketfy.Data." + key);
        try { IsAcquired = _mutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { IsAcquired = true; }
    }
    public void Dispose()
    {
        if (IsAcquired) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
