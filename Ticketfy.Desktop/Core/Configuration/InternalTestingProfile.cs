namespace Ticketfy.Core.Configuration;

/// <summary>Installed test editions must never default to the operational data directory.</summary>
internal static class InternalTestingProfile
{
    internal const string MarkerFileName = "ticketfy-internal-testing.txt";
    internal const string MarkerContents = "Ticketfy.InternalTesting.v1\n";
    internal const string InstallerMutex = "Ticketfy.InternalTesting";

    internal static (string Directory, bool IsInternalTesting) ResolveDefault(string applicationDirectory, string localApplicationData)
    {
        var marker = new FileInfo(Path.Combine(applicationDirectory, MarkerFileName));
        if (!marker.Exists) return (Path.Combine(localApplicationData, "Ticketfy"), false);
        if (marker.Length > 128 || marker.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            File.ReadAllText(marker.FullName) != MarkerContents)
            throw new InvalidOperationException("La identificación de la edición de pruebas está dañada. Reinstale el candidato verificado.");
        string directory = Path.Combine(localApplicationData, "Ticketfy.InternalTesting", "Data");
        ValidateDirectory(directory, localApplicationData);
        return (directory, true);
    }

    internal static void ValidateDirectory(string directory, string localApplicationData)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("La carpeta de pruebas debe ser absoluta.");
        string root = Path.GetFullPath(Path.Combine(localApplicationData, "Ticketfy.InternalTesting"));
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(full))
            throw new ArgumentException("Esta edición sólo admite datos dentro de la carpeta Ticketfy.InternalTesting del usuario.");
        for (var ancestor = new DirectoryInfo(full); ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("La carpeta de pruebas no admite redirecciones de directorio.");
        }
    }
}
