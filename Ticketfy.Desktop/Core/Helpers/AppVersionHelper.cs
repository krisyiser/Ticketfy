using System.Reflection;

namespace Ticketfy.Core.Helpers;

public static class AppVersionHelper
{
    /// <summary>
    /// Returns the current application version string (e.g. "v3.1.56").
    /// </summary>
    public static string DisplayVersion { get; } = GetDisplayVersion();

    /// <summary>
    /// Full application title string for window titles and about page.
    /// </summary>
    public static string FullTitle => $"TICKETFY! {DisplayVersion} — Enterprise Desktop Edition";

    private static string GetDisplayVersion()
    {
        string? informationalVersion = typeof(AppVersionHelper).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        string version = informationalVersion?.Split('+')[0]
            ?? typeof(AppVersionHelper).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        return $"v{version.TrimStart('v', 'V')}";
    }
}
