using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ticketfy.Core.Configuration;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;
using Ticketfy.Logging;

namespace Ticketfy.Services.Audit;

/// <summary>HMAC-chained, append-only administrative and transactional audit log.</summary>
public class AuditService : IAuditService
{
    private static readonly SemaphoreSlim AppendLock = new(1, 1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int CurrentSchemaVersion = 2;
    private readonly IDbContextFactory<AuditDbContext> _contextFactory;
    private readonly byte[] _integrityKey;

    public AuditService(IDbContextFactory<AuditDbContext> contextFactory, string? integrityKey = null)
    {
        _contextFactory = contextFactory;
        string key = integrityKey ?? Security.SecurityManager.GetOrCreateProtectedSecret(AppDataPaths.AuditIntegrityKeyFile);
        _integrityKey = Convert.FromBase64String(key);
    }

    public async Task LogAsync(AuditLogEntity log)
    {
        await AppendLock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(log.Id)) log.Id = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(log.TerminalName)) log.TerminalName = Environment.MachineName;
            log.Reason = LogSanitizer.Sanitize(log.Reason);
            log.OldValue = LogSanitizer.Sanitize(log.OldValue);
            log.NewValue = LogSanitizer.Sanitize(log.NewValue);

            using var context = await _contextFactory.CreateDbContextAsync();
            var previousEntry = await context.AuditLogs.AsNoTracking()
                .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
                .Select(a => new { a.Timestamp, a.EntryHash }).FirstOrDefaultAsync();
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            if (previousEntry != null && DateTimeOffset.TryParse(previousEntry.Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var previousTimestamp) && timestamp <= previousTimestamp)
                timestamp = previousTimestamp.AddTicks(1);
            log.Timestamp = timestamp.ToString("O", CultureInfo.InvariantCulture);
            log.PreviousHash = previousEntry?.EntryHash ?? string.Empty;
            log.SchemaVersion = CurrentSchemaVersion;
            log.EntryHash = ComputeHash(log);
            context.AuditLogs.Add(log);
            await DbResilienceHelper.ExecuteWithRetryAsync(() => context.SaveChangesAsync());

            Log.Information("AuditLog [{ActionType}] [{RiskLevel}] Entity: {EntityName}/{EntityId}, User: {UserId}, Impact: {Impact}",
                log.ActionType, log.RiskLevel, log.EntityName, log.EntityId, log.UserId, log.FinancialImpact);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to persist immutable audit log entry");
            throw;
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public Task LogAsync(string level, string message, string? meta = null) => LogAsync(new AuditLogEntity
    {
        UserId = "SYSTEM",
        ActionType = Core.Enums.AuditActionType.ShiftDrawerOpenedManually,
        RiskLevel = level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? Core.Enums.RiskLevel.Warning : Core.Enums.RiskLevel.Info,
        EntityName = "System",
        Reason = message,
        OldValue = meta ?? string.Empty
    });

    public async Task<List<AuditLogEntity>> GetRecentLogsAsync(int limit = 100)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.AuditLogs.AsNoTracking().OrderByDescending(a => a.Timestamp).Take(limit).ToListAsync();
    }

    public async Task<bool> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string previous = string.Empty;
        // One ordered SELECT provides a consistent snapshot without materializing the entire chain.
        await foreach (var entry in context.AuditLogs.AsNoTracking().OrderBy(a => a.Timestamp).ThenBy(a => a.Id)
            .AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidEntry(entry, previous)) return false;
            previous = entry.EntryHash;
        }
        return true;
    }

    public async Task InitializeIntegrityAsync(bool allowLegacyBootstrap = false, CancellationToken cancellationToken = default)
    {
        await AppendLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            bool unsigned = await context.AuditLogs.AnyAsync(a => a.EntryHash == "", cancellationToken);
            if (unsigned && (!allowLegacyBootstrap || await context.AuditLogs.AnyAsync(
                a => a.EntryHash != "" || a.SchemaVersion != 1 || a.PreviousHash != "", cancellationToken)))
                throw new InvalidDataException("La auditoría contiene firmas ausentes. Se requiere revisión; no se regeneran automáticamente.");

            string previous = string.Empty;
            string? afterTimestamp = null, afterId = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = context.AuditLogs.AsNoTracking();
                if (afterTimestamp is not null)
                    query = query.Where(a => string.Compare(a.Timestamp, afterTimestamp) > 0 ||
                        a.Timestamp == afterTimestamp && string.Compare(a.Id, afterId) > 0);
                var entries = await query.OrderBy(a => a.Timestamp).ThenBy(a => a.Id).Take(200).ToListAsync(cancellationToken);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (unsigned)
                    {
                        context.Attach(entry);
                        entry.PreviousHash = previous;
                        entry.SchemaVersion = CurrentSchemaVersion;
                        entry.EntryHash = ComputeHash(entry);
                    }
                    else if (!IsValidEntry(entry, previous))
                    {
                        throw new InvalidDataException($"La cadena de auditoría fue alterada en la entrada {entry.Id}.");
                    }
                    previous = entry.EntryHash;
                }
                if (unsigned)
                {
                    await context.SaveChangesAsync(cancellationToken);
                    context.ChangeTracker.Clear();
                }
                if (entries.Count < 200) break;
                afterTimestamp = entries[^1].Timestamp; afterId = entries[^1].Id;
            }
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER IF NOT EXISTS audit_logs_no_update BEFORE UPDATE ON audit_logs BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER IF NOT EXISTS audit_logs_no_delete BEFORE DELETE ON audit_logs BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            AppendLock.Release();
        }
    }

    private string ComputeHash(AuditLogEntity entry)
    {
        string?[] fields =
        [
            entry.SchemaVersion.ToString(CultureInfo.InvariantCulture), entry.Id, entry.Timestamp, entry.UserId,
            entry.AuthorizedBySupervisorId, ((int)entry.ActionType).ToString(CultureInfo.InvariantCulture),
            ((int)entry.RiskLevel).ToString(CultureInfo.InvariantCulture),
            entry.EntityName, entry.EntityId, entry.OldValue, entry.NewValue,
            entry.FinancialImpact.ToString("G29", CultureInfo.InvariantCulture), entry.Reason,
            entry.TerminalName, entry.PreviousHash
        ];
        byte[] canonical;
        switch (entry.SchemaVersion)
        {
            case 1:
                // Preserve existing signatures, but never attest delimiter-ambiguous legacy data.
                if (fields.Any(field => field?.Contains('\u001f') == true))
                    throw new InvalidDataException("La auditoría heredada contiene campos ambiguos; requiere revisión.");
                canonical = StrictUtf8.GetBytes(string.Join("\u001f", fields));
                break;
            case CurrentSchemaVersion:
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
                    {
                        writer.Write("Ticketfy.Audit.Entry/HMAC-SHA256");
                        writer.Write(fields.Length);
                        foreach (string? field in fields)
                        {
                            // Presence plus UTF-8 byte length separates every field, including null/empty.
                            writer.Write(field is not null);
                            if (field is not null) writer.Write(field);
                        }
                    }
                    canonical = stream.ToArray();
                }
                break;
            default:
                throw new InvalidDataException("Versión de integridad de auditoría no compatible.");
        }
        return Convert.ToBase64String(HMACSHA256.HashData(_integrityKey, canonical));
    }

    private bool IsValidEntry(AuditLogEntity entry, string previous)
    {
        if (!FixedEquals(entry.PreviousHash, previous)) return false;
        try { return FixedEquals(entry.EntryHash, ComputeHash(entry)); }
        catch (InvalidDataException) { return false; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool FixedEquals(string left, string right)
    {
        byte[] a = Encoding.UTF8.GetBytes(left ?? string.Empty);
        byte[] b = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
