using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Hardware;
using Ticketfy.Core.Helpers;
using Ticketfy.Core.Operations;
using Ticketfy.Logging;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class DiagnosticExportService : IDiagnosticExportService
{
    private const int MaximumLogCharacters = 250_000;
    private readonly string _logDirectory;
    private readonly string _backupDirectory;
    private readonly string _databasePath;
    private readonly string _defaultDestination;
    private readonly Func<DateTimeOffset> _utcNow;

    public DiagnosticExportService(
        string? logDirectory = null,
        string? backupDirectory = null,
        string? databasePath = null,
        string? defaultDestination = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _logDirectory = Path.GetFullPath(logDirectory ?? AppDataPaths.LogDirectory);
        _backupDirectory = Path.GetFullPath(backupDirectory ?? AppDataPaths.BackupDirectory);
        _databasePath = Path.GetFullPath(databasePath ?? AppDataPaths.DatabaseFile);
        _defaultDestination = Path.GetFullPath(defaultDestination ?? AppDataPaths.DiagnosticDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<DiagnosticExportResult> ExportAsync(string? destinationDirectory = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(destinationDirectory, cancellationToken), cancellationToken);

    private DiagnosticExportResult Export(string? destinationDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(destinationDirectory ?? _defaultDestination);
        Directory.CreateDirectory(destination);
        string timestamp = _utcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss");
        string archivePath = Path.Combine(destination, $"ticketfy-diagnostics-{timestamp}-{Guid.NewGuid():N}.zip");

        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "README.txt",
                    "Paquete de diagnóstico Ticketfy. No contiene bases de datos, credenciales, llaves, tokens ni archivos de licencia.\n");

                var report = new
                {
                    generatedAtUtc = _utcNow(),
                    applicationVersion = AppVersionHelper.DisplayVersion,
                    runtime = RuntimeInformation.FrameworkDescription,
                    operatingSystem = RuntimeInformation.OSDescription,
                    osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    database = new
                    {
                        exists = File.Exists(_databasePath),
                        sizeBytes = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0
                    },
                    backups = BuildBackupSummary(),
                    hardwareCompatibility = HardwareCompatibility.Matrix.Select(profile => new
                    {
                        profile.Id,
                        kind = profile.Kind.ToString(),
                        transport = profile.Transport.ToString(),
                        profile.Protocol,
                        certification = profile.Certification.ToString()
                    })
                };
                WriteEntry(archive, "environment.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

                if (Directory.Exists(_logDirectory))
                {
                    foreach (FileInfo log in new DirectoryInfo(_logDirectory).EnumerateFiles("*.*")
                                 .Where(file => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                                     || file.Name.Equals("crash_log.txt", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(file => file.LastWriteTimeUtc)
                                 .Take(3))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string content = ReadTail(log.FullName, MaximumLogCharacters);
                        WriteEntry(archive, $"logs/{SanitizeArchiveName(log.Name)}", LogSanitizer.Sanitize(content));
                    }
                }
            }

            return new(archivePath, ComputeSha256(archivePath));
        }
        catch
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
    }

    private object BuildBackupSummary()
    {
        if (!Directory.Exists(_backupDirectory)) return new { count = 0, newestUtc = (DateTimeOffset?)null };
        FileInfo[] backups = new DirectoryInfo(_backupDirectory).EnumerateFiles("backup_*.db")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        return new
        {
            count = backups.Length,
            newestUtc = backups.Length == 0 ? (DateTimeOffset?)null : backups[0].LastWriteTimeUtc
        };
    }

    private static string ReadTail(string path, int maximumCharacters)
    {
        string content = File.ReadAllText(path);
        return content.Length <= maximumCharacters ? content : content[^maximumCharacters..];
    }

    private static string SanitizeArchiveName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
