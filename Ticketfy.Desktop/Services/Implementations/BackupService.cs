using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ticketfy.Core.Configuration;
using Ticketfy.Services.Security;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public class BackupService : IBackupService
{
    private const long MinimumBackupHeadroomBytes = 1_048_576;
    private readonly string _dbFilePath;
    private readonly string _backupDirectory;
    private readonly Func<string, long> _availableSpaceProvider;

    public BackupService(
        string? databaseFilePath = null,
        string? backupDirectory = null,
        Func<string, long>? availableSpaceProvider = null)
    {
        _dbFilePath = databaseFilePath ?? AppDataPaths.DatabaseFile;
        _backupDirectory = backupDirectory ?? AppDataPaths.BackupDirectory;
        _availableSpaceProvider = availableSpaceProvider ?? GetAvailableSpace;
    }

    public Task<string> CreateBackupAsync(string reference = "Manual")
    {
        return Task.Run(() =>
        {
            if (!File.Exists(_dbFilePath))
            {
                throw new FileNotFoundException("No se encontró la base de datos que se debe respaldar.", _dbFilePath);
            }

            long requiredSpace = checked(new FileInfo(_dbFilePath).Length + MinimumBackupHeadroomBytes);
            if (_availableSpaceProvider(_backupDirectory) < requiredSpace)
            {
                throw new IOException(
                    $"Espacio insuficiente para crear el respaldo. Se requieren al menos {requiredSpace} bytes libres.");
            }

            Directory.CreateDirectory(_backupDirectory);
            string safeReference = SanitizeReference(reference);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupFileName = $"backup_{safeReference}_{timestamp}_{Guid.NewGuid():N}.db";
            string destinationPath = Path.Combine(_backupDirectory, backupFileName);

            string password = SecurityManager.GetMasterKey();
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbFilePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            sourceBuilder.Password = password;
            destinationBuilder.Password = password;

            try
            {
                using var source = new SqliteConnection(sourceBuilder.ConnectionString);
                using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);

                using var integrityCommand = destination.CreateCommand();
                integrityCommand.CommandText = "PRAGMA integrity_check;";
                string? integrityResult = integrityCommand.ExecuteScalar()?.ToString();
                if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"El respaldo no superó la verificación de integridad: {integrityResult}");
                }

                // Close explicitly before completing the worker task. This keeps
                // Windows from retaining the source handle until finalization.
                integrityCommand.Dispose();
                destination.Close();
                source.Close();
                return destinationPath;
            }
            catch
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                throw;
            }
        });
    }

    public async Task<bool> CreateZCutBackupAsync(string shiftReference)
    {
        try
        {
            await CreateBackupAsync($"ZCut_{shiftReference}");
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error creating Z-Cut backup: {Message}", ex.Message);
            return false;
        }
    }

    private static string SanitizeReference(string reference)
    {
        string value = string.IsNullOrWhiteSpace(reference) ? "Manual" : reference.Trim();
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }
        return value;
    }

    private static long GetAvailableSpace(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("No se pudo determinar la unidad del directorio de respaldos.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
