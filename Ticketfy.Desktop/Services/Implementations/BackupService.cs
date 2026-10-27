using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Interfaces;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

public sealed class BackupService : IBackupService
{
    private const long MinimumBackupHeadroomBytes = 1_048_576;
    private static readonly string[] RequiredCommercialTables = ["products", "sales", "usuarios", "settings"];
    private readonly string _dbFilePath;
    private readonly string _backupDirectory;
    private readonly string _pendingRestorePath;
    private readonly string _pendingManifestPath;
    private readonly string _maintenanceStatePath;
    private readonly Func<string, long> _availableSpaceProvider;
    private readonly Func<string> _passwordProvider;
    private readonly Func<DateTimeOffset> _utcNow;

    public BackupService(
        string? databaseFilePath = null,
        string? backupDirectory = null,
        Func<string, long>? availableSpaceProvider = null,
        Func<string>? passwordProvider = null,
        Func<DateTimeOffset>? utcNow = null,
        string? pendingRestorePath = null,
        string? pendingManifestPath = null,
        string? maintenanceStatePath = null)
    {
        _dbFilePath = Path.GetFullPath(databaseFilePath ?? AppDataPaths.DatabaseFile);
        _backupDirectory = Path.GetFullPath(backupDirectory ?? AppDataPaths.BackupDirectory);
        _pendingRestorePath = Path.GetFullPath(pendingRestorePath ?? AppDataPaths.PendingRestoreFile);
        _pendingManifestPath = Path.GetFullPath(pendingManifestPath ?? AppDataPaths.PendingRestoreManifestFile);
        _maintenanceStatePath = Path.GetFullPath(maintenanceStatePath ?? Path.Combine(_backupDirectory, "maintenance-state.json"));
        _availableSpaceProvider = availableSpaceProvider ?? GetAvailableSpace;
        _passwordProvider = passwordProvider ?? SecurityManager.GetMasterKey;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<string> CreateBackupAsync(string reference = "Manual", CancellationToken cancellationToken = default) =>
        Task.Run(() => CreateBackup(reference, cancellationToken), cancellationToken);

    public async Task<bool> CreateZCutBackupAsync(string shiftReference, CancellationToken cancellationToken = default)
    {
        try
        {
            await CreateBackupAsync($"ZCut_{shiftReference}", cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Error(ex, "Error creating Z-Cut backup: {Message}", ex.Message);
            return false;
        }
    }

    public Task<BackupValidationResult> ValidateBackupAsync(string backupPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => ValidateBackup(backupPath, requireCommercialSchema: true, cancellationToken), cancellationToken);

    public Task<BackupValidationResult> TestRestoreAsync(string backupPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => TestRestore(backupPath, cancellationToken), cancellationToken);

    public async Task<RestorePreparationResult> PrepareRestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        string sourcePath = Path.GetFullPath(backupPath);
        if (string.Equals(sourcePath, _dbFilePath, StringComparison.OrdinalIgnoreCase))
            return new(false, "La base activa no puede seleccionarse como respaldo.");

        BackupValidationResult validation = await ValidateBackupAsync(sourcePath, cancellationToken);
        if (!validation.IsValid) return new(false, validation.Message);

        string? safetyBackup = File.Exists(_dbFilePath)
            ? await CreateBackupAsync("BeforeRestore", cancellationToken)
            : null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingRestorePath)!);
            DeleteIfExists(_pendingRestorePath);
            CopyDatabaseWithSqliteBackupApi(sourcePath, _pendingRestorePath, cancellationToken);

            BackupValidationResult staged = ValidateBackup(_pendingRestorePath, requireCommercialSchema: true, cancellationToken);
            if (!staged.IsValid) throw new InvalidDataException(staged.Message);

            var manifest = new PendingRestoreManifest(staged.Sha256!, _utcNow(), safetyBackup);
            WriteJsonAtomically(_pendingManifestPath, manifest);
            return new(true,
                "El respaldo fue validado y quedó preparado. Reinicie Ticketfy para aplicar la restauración.",
                _pendingRestorePath,
                safetyBackup);
        }
        catch
        {
            DeleteIfExists(_pendingRestorePath);
            DeleteIfExists(_pendingManifestPath);
            throw;
        }
    }

    public Task<bool> ApplyPendingRestoreAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ApplyPendingRestore(cancellationToken), cancellationToken);

    public async Task<BackupMaintenanceResult> RunMaintenanceAsync(BackupPolicy policy, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled) return new(false, true, "Los respaldos automáticos están deshabilitados.");
        ValidatePolicy(policy);

        MaintenanceState state = ReadMaintenanceState();
        if (state.LastSuccessUtc is DateTimeOffset lastSuccess && _utcNow() - lastSuccess < policy.Interval)
            return new(false, true, "El respaldo automático todavía no está programado.");

        try
        {
            string backupPath = await CreateBackupAsync("Automatic", cancellationToken);
            if (policy.VerifyAfterCreation)
            {
                BackupValidationResult restoreTest = await TestRestoreAsync(backupPath, cancellationToken);
                if (!restoreTest.IsValid) throw new InvalidDataException(restoreTest.Message);
            }

            int deleted = ApplyRetention(policy, backupPath);
            if (!TryWriteMaintenanceState(new MaintenanceState(_utcNow(), null, Path.GetFileName(backupPath))))
                return new(true, false, "El respaldo fue creado, pero no se pudo guardar el estado de programación.", backupPath, deleted);
            return new(true, true, policy.VerifyAfterCreation
                ? "Respaldo automático creado y prueba de restauración verificada."
                : "Respaldo automático creado; prueba de restauración desactivada.", backupPath, deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryWriteMaintenanceState(new MaintenanceState(state.LastSuccessUtc, _utcNow(), null));
            Serilog.Log.Error(ex, "Automatic backup maintenance failed");
            return new(true, false, "No se pudo completar el respaldo automático: " + ex.Message);
        }
    }

    private string CreateBackup(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_dbFilePath))
            throw new FileNotFoundException("No se encontró la base de datos que se debe respaldar.", _dbFilePath);

        long requiredSpace = checked(new FileInfo(_dbFilePath).Length + MinimumBackupHeadroomBytes);
        if (_availableSpaceProvider(_backupDirectory) < requiredSpace)
            throw new IOException($"Espacio insuficiente para crear el respaldo. Se requieren al menos {requiredSpace} bytes libres.");
        Directory.CreateDirectory(_backupDirectory);

        string timestamp = _utcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss");
        string destinationPath = Path.Combine(_backupDirectory,
            $"backup_{SanitizeReference(reference)}_{timestamp}_{Guid.NewGuid():N}.db");

        try
        {
            CopyDatabaseWithSqliteBackupApi(_dbFilePath, destinationPath, cancellationToken);
            BackupValidationResult validation = ValidateBackup(destinationPath, requireCommercialSchema: false, cancellationToken);
            if (!validation.IsValid) throw new InvalidDataException(validation.Message);
            return destinationPath;
        }
        catch
        {
            DeleteIfExists(destinationPath);
            throw;
        }
    }

    private BackupValidationResult TestRestore(string backupPath, CancellationToken cancellationToken)
    {
        string sourcePath = Path.GetFullPath(backupPath);
        BackupValidationResult validation = ValidateBackup(sourcePath, requireCommercialSchema: true, cancellationToken);
        if (!validation.IsValid) return validation;

        Directory.CreateDirectory(_backupDirectory);
        string testPath = Path.Combine(_backupDirectory, $".restore-test-{Guid.NewGuid():N}.db");
        try
        {
            CopyDatabaseWithSqliteBackupApi(sourcePath, testPath, cancellationToken);
            return ValidateBackup(testPath, requireCommercialSchema: true, cancellationToken) with
            {
                Message = "El respaldo se restauró en un entorno temporal y superó la verificación."
            };
        }
        finally
        {
            DeleteIfExists(testPath);
        }
    }

    private bool ApplyPendingRestore(CancellationToken cancellationToken)
    {
        if (!File.Exists(_pendingRestorePath) && !File.Exists(_pendingManifestPath)) return false;
        if (!File.Exists(_pendingRestorePath) || !File.Exists(_pendingManifestPath))
            throw new InvalidDataException("La restauración pendiente está incompleta y no se aplicó.");

        PendingRestoreManifest manifest = JsonSerializer.Deserialize<PendingRestoreManifest>(File.ReadAllText(_pendingManifestPath))
            ?? throw new InvalidDataException("El manifiesto de restauración no es válido.");
        BackupValidationResult pending = ValidateBackup(_pendingRestorePath, requireCommercialSchema: true, cancellationToken);
        if (!pending.IsValid || !string.Equals(pending.Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La restauración pendiente cambió o no superó la validación.");

        Directory.CreateDirectory(Path.GetDirectoryName(_dbFilePath)!);
        string rollbackPath = _dbFilePath + ".restore.rollback";
        DeleteIfExists(rollbackPath);
        DeleteIfExists(_dbFilePath + "-wal");
        DeleteIfExists(_dbFilePath + "-shm");

        try
        {
            if (File.Exists(_dbFilePath)) File.Replace(_pendingRestorePath, _dbFilePath, rollbackPath, ignoreMetadataErrors: true);
            else File.Move(_pendingRestorePath, _dbFilePath);

            BackupValidationResult applied = ValidateBackup(_dbFilePath, requireCommercialSchema: true, cancellationToken);
            if (!applied.IsValid) throw new InvalidDataException(applied.Message);
            DeleteIfExists(_pendingManifestPath);
            DeleteIfExists(rollbackPath);
            Serilog.Log.Warning("A verified pending database restore was applied during startup");
            return true;
        }
        catch
        {
            if (File.Exists(rollbackPath))
            {
                DeleteIfExists(_dbFilePath);
                File.Move(rollbackPath, _dbFilePath);
            }
            throw;
        }
    }

    private BackupValidationResult ValidateBackup(string path, bool requireCommercialSchema, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return new(false, "No se encontró el archivo de respaldo.");
            if (new FileInfo(fullPath).Length == 0) return new(false, "El archivo de respaldo está vacío.");

            using var connection = OpenConnection(fullPath, SqliteOpenMode.ReadOnly);
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            string? result = integrity.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                return new(false, $"El respaldo no superó la verificación de integridad: {result}");

            if (requireCommercialSchema)
            {
                foreach (string table in RequiredCommercialTables)
                {
                    using var tableCheck = connection.CreateCommand();
                    tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND lower(name)=lower($name);";
                    tableCheck.Parameters.AddWithValue("$name", table);
                    if (Convert.ToInt32(tableCheck.ExecuteScalar()) == 0)
                        return new(false, $"El respaldo no contiene la tabla comercial requerida '{table}'.");
                }
            }

            var file = new FileInfo(fullPath);
            return new(true, "El respaldo es íntegro y compatible.", ComputeSha256(fullPath), file.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, "No se pudo validar el respaldo: " + ex.Message);
        }
    }

    private void CopyDatabaseWithSqliteBackupApi(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfExists(destinationPath);
        using var source = OpenConnection(sourcePath, SqliteOpenMode.ReadOnly);
        using var destination = OpenConnection(destinationPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            Password = _passwordProvider()
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private int ApplyRetention(BackupPolicy policy, string protectedBackupPath)
    {
        var backups = new DirectoryInfo(_backupDirectory)
            .EnumerateFiles("backup_*.db", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        DateTimeOffset cutoff = _utcNow() - policy.RetentionAge;
        int deleted = 0;
        for (int index = 0; index < backups.Length; index++)
        {
            FileInfo file = backups[index];
            bool protectedFile = string.Equals(file.FullName, protectedBackupPath, StringComparison.OrdinalIgnoreCase);
            bool expired = file.LastWriteTimeUtc < cutoff.UtcDateTime;
            bool exceedsCount = index >= policy.RetentionCount;
            if (!protectedFile && (expired || exceedsCount))
            {
                file.Delete();
                deleted++;
            }
        }
        return deleted;
    }

    private MaintenanceState ReadMaintenanceState()
    {
        try
        {
            return File.Exists(_maintenanceStatePath)
                ? JsonSerializer.Deserialize<MaintenanceState>(File.ReadAllText(_maintenanceStatePath)) ?? new(null, null, null)
                : new(null, null, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(null, null, null);
        }
    }

    private bool TryWriteMaintenanceState(MaintenanceState state)
    {
        try
        {
            WriteJsonAtomically(_maintenanceStatePath, state);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not persist backup maintenance state");
            return false;
        }
    }

    private static void ValidatePolicy(BackupPolicy policy)
    {
        if (policy.Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policy), "El intervalo debe ser positivo.");
        if (policy.RetentionCount < 1) throw new ArgumentOutOfRangeException(nameof(policy), "Debe conservarse al menos un respaldo.");
        if (policy.RetentionAge < TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(policy), "La retención mínima es un día.");
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SanitizeReference(string reference)
    {
        string value = string.IsNullOrWhiteSpace(reference) ? "Manual" : reference.Trim();
        foreach (char invalidChar in Path.GetInvalidFileNameChars()) value = value.Replace(invalidChar, '_');
        return value;
    }

    private static long GetAvailableSpace(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException("No se pudo determinar la unidad del directorio de respaldos.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record PendingRestoreManifest(string Sha256, DateTimeOffset PreparedAtUtc, string? SafetyBackupPath);
    private sealed record MaintenanceState(DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastFailureUtc, string? LastBackupFile);
}
