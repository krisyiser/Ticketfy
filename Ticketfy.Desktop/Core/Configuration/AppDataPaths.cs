using System;
using System.IO;
using Ticketfy.Core.Constants;

namespace Ticketfy.Core.Configuration;

/// <summary>
/// Canonical filesystem locations for all persistent Ticketfy data.
/// Keeping these paths in one place prevents backup, startup and runtime code
/// from silently targeting different databases.
/// </summary>
public static class AppDataPaths
{
    private static string? _selectedDataDirectory;
    private static readonly Lazy<(string Directory, bool IsInternalTesting)> DefaultProfile = new(() =>
        InternalTestingProfile.ResolveDefault(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    public static string RootDirectory => _selectedDataDirectory ?? DefaultProfile.Value.Directory;
    internal static bool IsInternalTesting => DefaultProfile.Value.IsInternalTesting;

    /// <summary>Explicit startup selection for a separately restored installation or isolated laboratory.</summary>
    internal static void UseDataDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("La carpeta de datos debe ser una ruta absoluta.");
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length <= (Path.GetPathRoot(full)?.Length ?? 0) || File.Exists(full))
            throw new ArgumentException("Seleccione una carpeta de datos específica, no la raíz del disco ni un archivo.");
        _selectedDataDirectory = full;
    }

    public static string DatabaseDirectory => Path.Combine(RootDirectory, "Database");
    public static string DatabaseFile => Path.Combine(DatabaseDirectory, AppConstants.DatabaseFileName);
    public static string AuditDatabaseFile => Path.Combine(DatabaseDirectory, AppConstants.AuditDatabaseFileName);
    public static string BackupDirectory => Path.Combine(RootDirectory, "Backups");
    public static string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public static string DiagnosticDirectory => Path.Combine(RootDirectory, "Diagnostics");
    public static string TelemetryDirectory => Path.Combine(RootDirectory, "Telemetry");
    public static string PendingRestoreFile => Path.Combine(DatabaseDirectory, "ticketfy.restore.pending.db");
    public static string PendingRestoreManifestFile => Path.Combine(DatabaseDirectory, "ticketfy.restore.pending.json");
    public static string BackupMaintenanceStateFile => Path.Combine(BackupDirectory, "maintenance-state.json");
    public static string FiscalDocumentsDirectory => Path.Combine(RootDirectory, "FiscalDocuments");
    public static string SecretDirectory => Path.Combine(RootDirectory, "Secrets");
    public static string CredentialDirectory => Path.Combine(SecretDirectory, "Credentials");
    public static string MasterKeyFile => Path.Combine(SecretDirectory, "database-key.dpapi");
    public static string AuditIntegrityKeyFile => Path.Combine(SecretDirectory, "audit-integrity-key.dpapi");
    public static string LicenseStateFile => Path.Combine(SecretDirectory, "license-state.dpapi");
    public static string LicenseFile => Path.Combine(RootDirectory, "license.jwt");

    public static void EnsureRequiredDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SecretDirectory);
        Directory.CreateDirectory(CredentialDirectory);
    }
}
