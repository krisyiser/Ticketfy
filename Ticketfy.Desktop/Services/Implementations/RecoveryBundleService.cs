using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Constants;
using Ticketfy.Core.Security;
using Ticketfy.Data;
using Ticketfy.Services.Audit;
using Ticketfy.Services.Security;

namespace Ticketfy.Services.Implementations;

public sealed record RecoveryBundleResult(string Directory, DateTimeOffset CapturedAtUtc, string Message);

/// <summary>Portable, authenticated recovery of data and keys. Never overwrites an installation.</summary>
public sealed class RecoveryBundleService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TFYREC01");
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    private const int MaximumPayloadBytes = 256 * 1024 * 1024;
    private const int HeaderSize = 8 + SaltSize + NonceSize;
    // Part of the TFYREC01 wire format; changing authentication policy must not break old backups.
    private const int FormatOneKdfIterations = 600_000;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext _operations;
    private readonly string _database, _auditDatabase;
    private readonly Func<string> _masterKey, _auditKey;

    public RecoveryBundleService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext operations,
        string? database = null, string? auditDatabase = null, Func<string>? masterKey = null, Func<string>? auditKey = null)
    {
        _factory = factory; _operations = operations;
        _database = Path.GetFullPath(database ?? AppDataPaths.DatabaseFile);
        _auditDatabase = Path.GetFullPath(auditDatabase ?? AppDataPaths.AuditDatabaseFile);
        _masterKey = masterKey ?? SecurityManager.GetMasterKey;
        _auditKey = auditKey ?? (() => new DpapiSecretStore(AppDataPaths.AuditIntegrityKeyFile).Read());
    }

    public async Task<string> CreateAsync(string destination, string passphrase, CancellationToken cancellationToken = default)
    {
        ValidatePassphrase(passphrase);
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Elija un archivo nuevo; no se sobrescriben respaldos.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("Seleccione una carpeta de destino existente.");
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
        {
            await _operations.RequireAsync(db, PermissionCatalog.SettingsSecurity, false);
            if (await db.Shifts.AnyAsync(s => s.IsOpen == 1, cancellationToken))
                throw new InvalidOperationException("Cierre los turnos antes de crear el paquete de recuperación integral.");
        }
        return await Task.Run(async () =>
        {
            // Plain audit data stays in a private local Windows directory, never on an unprotected USB/FAT destination.
            string stage = CreateStage(Path.GetTempPath());
            string encrypted = Path.Combine(Path.GetDirectoryName(destination)!, ".ticketfy-encrypted-" + Guid.NewGuid().ToString("N") + ".tmp");
            byte[]? archive = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = _masterKey(), integrity = _auditKey();
                if (new FileInfo(_database).Length + new FileInfo(_auditDatabase).Length > 2L * MaximumPayloadBytes)
                    throw new InvalidOperationException("Las bases exceden el límite de recuperación de 512 MiB. Requieren un procedimiento de gran volumen.");
                var manifest = new RecoveryManifest(1, DateTimeOffset.UtcNow, key, integrity, "", "");
                string dataCopy = Path.Combine(stage, "commercial.db"), auditCopy = Path.Combine(stage, "audit.db");
                CopySnapshot(_database, dataCopy, key, requireClosedRegisters: true);
                CopySnapshot(_auditDatabase, auditCopy, "", requireClosedRegisters: false);
                await ValidateDatabasesAsync(dataCopy, auditCopy, manifest, cancellationToken);
                manifest = manifest with { DatabaseSha256 = Hash(dataCopy), AuditSha256 = Hash(auditCopy) };
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    AddFile(zip, "commercial.db", dataCopy);
                    AddFile(zip, "audit.db", auditCopy);
                    using var metadata = zip.CreateEntry("recovery.json").Open();
                    JsonSerializer.Serialize(metadata, manifest);
                }
                if (memory.Length > MaximumPayloadBytes) throw new InvalidOperationException("El paquete excede 256 MiB comprimidos. Requiere un procedimiento de recuperación de gran volumen.");
                archive = memory.ToArray();
                CryptographicOperations.ZeroMemory(memory.GetBuffer());
                await WriteEncryptedAsync(encrypted, archive, passphrase, cancellationToken);
                File.Move(encrypted, destination, overwrite: false);
                return destination;
            }
            finally
            {
                if (archive is not null) CryptographicOperations.ZeroMemory(archive);
                if (File.Exists(encrypted)) File.Delete(encrypted);
                DeleteStage(stage);
            }
        }, cancellationToken);
    }

    public static async Task<RecoveryBundleResult> VerifyAsync(string bundle, string passphrase, string testParent,
        CancellationToken cancellationToken = default)
    {
        string parent = Path.GetFullPath(testParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("La carpeta de prueba no existe.");
        string stage = CreateStage(parent);
        try
        {
            var manifest = await UnpackAndValidateAsync(bundle, passphrase, stage, cancellationToken);
            return new("", manifest.CapturedAtUtc, "Paquete íntegro: base comercial, auditoría y llaves verificadas en una copia aislada. No se modificó la instalación.");
        }
        finally { DeleteStage(stage); }
    }

    public static async Task<RecoveryBundleResult> RestoreToNewDirectoryAsync(string bundle, string passphrase, string destination,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Indique una ruta absoluta para la carpeta nueva de recuperación.");
        destination = Path.GetFullPath(destination);
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent) || File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidOperationException("La recuperación requiere una carpeta NUEVA dentro de una carpeta existente. Nunca sobrescribe una instalación.");
        string stage = CreateStage(parent);
        try
        {
            var manifest = await UnpackAndValidateAsync(bundle, passphrase, stage, cancellationToken);
            string ready = Path.Combine(stage, "restored");
            Directory.CreateDirectory(Path.Combine(ready, "Database"));
            Directory.CreateDirectory(Path.Combine(ready, "Secrets"));
            // Reprotect under the destination Windows identity, not the source profile's DPAPI blob.
            new DpapiSecretStore(Path.Combine(ready, "Secrets", "database-key.dpapi")).Write(manifest.MasterKey);
            new DpapiSecretStore(Path.Combine(ready, "Secrets", "audit-integrity-key.dpapi")).Write(manifest.AuditIntegrityKey);
            File.Move(Path.Combine(stage, "commercial.db"), Path.Combine(ready, "Database", AppConstants.DatabaseFileName));
            File.Move(Path.Combine(stage, "audit.db"), Path.Combine(ready, "Database", AppConstants.AuditDatabaseFileName));
            string recoveredDatabase = Path.Combine(ready, "Database", AppConstants.DatabaseFileName);
            await RebaseRecoveredBackupPathAsync(recoveredDatabase, manifest.MasterKey, destination, cancellationToken);
            var receipt = new
            {
                format = 1,
                captured_at_utc = manifest.CapturedAtUtc,
                restored_at_utc = DateTimeOffset.UtcNow,
                database_sha256 = manifest.DatabaseSha256,
                audit_sha256 = manifest.AuditSha256,
                restored_database_sha256 = Hash(recoveredDatabase),
                backup_path_rebased_to_destination = true,
                integrations_require_reconfiguration = true,
                license_requires_validation = true
            };
            await File.WriteAllTextAsync(Path.Combine(ready, "recovery-receipt.json"), JsonSerializer.Serialize(receipt), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Same-volume rename is the only publication point. A failed preparation leaves no half-installation.
            Directory.Move(ready, destination);
            return new(destination, manifest.CapturedAtUtc,
                "Recuperación verificada en carpeta nueva. Credenciales externas y licencia no se clonan; requieren configuración y validación. Facturama permanece desactivado.");
        }
        finally { DeleteStage(stage); }
    }

    private static async Task RebaseRecoveredBackupPathAsync(string database, string key, string destination, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString(database, key, SqliteOpenMode.ReadWrite)).Options;
        await using var db = new AppDbContext(options);
        var stored = await db.Settings.SingleOrDefaultAsync(s => s.Key == "SystemGlobalAppSettings_V4", ct);
        if (stored is null) return; // Fresh/default settings resolve from the explicitly selected new data root.
        var settings = JsonSerializer.Deserialize<Ticketfy.Core.Models.Settings.AppSettings>(stored.Value)
            ?? throw new InvalidDataException("Configuración recuperada inválida.");
        settings.System.BackupPath = Path.Combine(destination, "Backups");
        stored.Value = JsonSerializer.Serialize(settings);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<RecoveryManifest> UnpackAndValidateAsync(string bundle, string passphrase, string stage, CancellationToken ct)
    {
        byte[] archive = await ReadEncryptedAsync(bundle, passphrase, ct);
        try
        {
            using var memory = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            string[] expected = ["commercial.db", "audit.db", "recovery.json"];
            if (zip.Entries.Count != expected.Length || expected.Any(name => zip.Entries.Count(e => e.FullName == name) != 1))
                throw new InvalidDataException("El paquete no tiene el inventario exacto de recuperación.");
            if (zip.GetEntry("recovery.json")!.Length > 16384 || zip.Entries.Sum(e => e.Length) > 2L * MaximumPayloadBytes)
                throw new InvalidDataException("El contenido descomprimido excede los límites de recuperación.");
            RecoveryManifest manifest;
            using (var metadata = zip.GetEntry("recovery.json")!.Open())
                manifest = await JsonSerializer.DeserializeAsync<RecoveryManifest>(metadata, cancellationToken: ct)
                    ?? throw new InvalidDataException("Manifiesto vacío.");
            if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.MasterKey) || manifest.MasterKey.Length > 1024
                || string.IsNullOrWhiteSpace(manifest.AuditIntegrityKey) || Convert.FromBase64String(manifest.AuditIntegrityKey).Length != 32)
                throw new InvalidDataException("Versión o llaves de recuperación inválidas.");
            foreach (string name in expected.Take(2))
            {
                await using var input = zip.GetEntry(name)!.Open();
                await using var output = new FileStream(Path.Combine(stage, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, ct);
            }
            string data = Path.Combine(stage, "commercial.db"), audit = Path.Combine(stage, "audit.db");
            if (Hash(data) != manifest.DatabaseSha256 || Hash(audit) != manifest.AuditSha256)
                throw new InvalidDataException("Las bases no coinciden con el manifiesto autenticado.");
            await ValidateDatabasesAsync(data, audit, manifest, ct);
            return manifest;
        }
        finally { CryptographicOperations.ZeroMemory(archive); }
    }

    private static async Task ValidateDatabasesAsync(string data, string audit, RecoveryManifest manifest, CancellationToken ct)
    {
        ValidateSqlite(data, manifest.MasterKey);
        ValidateSqlite(audit, "");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString(data, manifest.MasterKey, SqliteOpenMode.ReadOnly)).Options;
        await using var db = new AppDbContext(options);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.Length == 0 || applied.Except(db.Database.GetMigrations()).Any())
            throw new InvalidDataException("Esquema no reconocido. Use una versión de Ticketfy compatible con el respaldo.");
        var auditOptions = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(ConnectionString(audit, "", SqliteOpenMode.ReadOnly)).Options;
        var auditService = new AuditService(new AppDbContextFactoryImpl<AuditDbContext>(auditOptions), manifest.AuditIntegrityKey);
        if (!await auditService.VerifyIntegrityAsync(ct)) throw new InvalidDataException("La cadena de auditoría no coincide con la llave de integridad.");
        ct.ThrowIfCancellationRequested();
    }

    private static void CopySnapshot(string sourcePath, string destinationPath, string password, bool requireClosedRegisters)
    {
        using var source = new SqliteConnection(ConnectionString(sourcePath, password, SqliteOpenMode.ReadOnly));
        source.Open();
        using var snapshot = source.BeginTransaction(deferred: true);
        using var command = source.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = requireClosedRegisters ? "SELECT COUNT(*) FROM shifts WHERE isOpen=1;" : "SELECT COUNT(*) FROM audit_logs;";
        long count = (long)command.ExecuteScalar()!;
        if (requireClosedRegisters && count != 0) throw new InvalidOperationException("Se abrió un turno durante la preparación; cierre los turnos y reintente.");
        using var destination = new SqliteConnection(ConnectionString(destinationPath, password, SqliteOpenMode.ReadWriteCreate));
        destination.Open();
        // Keep like-for-like encryption modes, as required by SQLCipher's Backup API.
        source.BackupDatabase(destination);
    }

    private static void ValidateSqlite(string path, string password)
    {
        using var db = new SqliteConnection(ConnectionString(path, password, SqliteOpenMode.ReadOnly));
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal)) throw new InvalidDataException("La base no superó integrity_check.");
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("La base tiene referencias inconsistentes.");
    }

    private static string ConnectionString(string path, string password, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    { DataSource = path, Password = password, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString();

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length is < 20 or > 1024)
            throw new ArgumentException("Use una frase de recuperación de 20 a 1024 caracteres y guárdela fuera del equipo. No podrá recuperarse si la pierde.");
    }

    private static async Task WriteEncryptedAsync(string file, byte[] plaintext, string passphrase, CancellationToken ct)
    {
        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        RandomNumberGenerator.Fill(header.AsSpan(8, SaltSize));
        RandomNumberGenerator.Fill(header.AsSpan(8 + SaltSize, NonceSize));
        byte[] password = Encoding.UTF8.GetBytes(passphrase);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(8, SaltSize), FormatOneKdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            byte[] cipher = new byte[plaintext.Length], tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(header.AsSpan(8 + SaltSize, NonceSize), plaintext, cipher, tag, header);
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(header, ct); await output.WriteAsync(tag, ct); await output.WriteAsync(cipher, ct);
            output.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<byte[]> ReadEncryptedAsync(string file, string passphrase, CancellationToken ct)
    {
        ValidatePassphrase(passphrase);
        var info = new FileInfo(file);
        if (!info.Exists || info.Length < HeaderSize + TagSize || info.Length > MaximumPayloadBytes + HeaderSize + TagSize)
            throw new InvalidDataException("Tamaño de paquete inválido o superior a 256 MiB.");
        byte[] bytes = await File.ReadAllBytesAsync(file, ct);
        if (!bytes.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Formato de paquete no reconocido.");
        byte[] password = Encoding.UTF8.GetBytes(passphrase);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, bytes.AsSpan(8, SaltSize), FormatOneKdfIterations, HashAlgorithmName.SHA256, 32);
        byte[] plaintext = new byte[bytes.Length - HeaderSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(bytes.AsSpan(8 + SaltSize, NonceSize), bytes.AsSpan(HeaderSize + TagSize), bytes.AsSpan(HeaderSize, TagSize), plaintext, bytes.AsSpan(0, HeaderSize));
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("Frase de recuperación incorrecta o paquete alterado. No se ha restaurado ningún dato.");
        }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(key); }
    }

    private static void AddFile(ZipArchive zip, string name, string path)
    {
        using var input = File.OpenRead(path);
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        input.CopyTo(entry);
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static string CreateStage(string parent)
    {
        string stage = Path.Combine(parent, ".ticketfy-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User
                    ?? throw new UnauthorizedAccessException("No se pudo determinar la identidad Windows para proteger los archivos temporales.");
                var permissions = new System.Security.AccessControl.DirectorySecurity();
                permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                permissions.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(stage), permissions);
            }
            catch { Directory.Delete(stage); throw; }
        }
        return stage;
    }
    private static void DeleteStage(string stage)
    {
        // Only the freshly allocated private work directory, never a caller's destination or installation.
        if (!Path.GetFileName(stage).StartsWith(".ticketfy-recovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Ruta temporal inválida.");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
    }
    private sealed record RecoveryManifest(int Version, DateTimeOffset CapturedAtUtc, string MasterKey, string AuditIntegrityKey,
        string DatabaseSha256, string AuditSha256);
}
