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
            log.SchemaVersion = 1;
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
        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.AuditLogs.AsNoTracking().OrderByDescending(a => a.Timestamp).Take(limit).ToListAsync();
    }

    public async Task<bool> VerifyIntegrityAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var entries = await context.AuditLogs.AsNoTracking().OrderBy(a => a.Timestamp).ThenBy(a => a.Id).ToListAsync();
        string previous = string.Empty;
        foreach (var entry in entries)
        {
            if (!FixedEquals(entry.PreviousHash, previous) || !FixedEquals(entry.EntryHash, ComputeHash(entry))) return false;
            previous = entry.EntryHash;
        }
        return true;
    }

    public async Task InitializeIntegrityAsync()
    {
        await AppendLock.WaitAsync();
        try
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var entries = await context.AuditLogs.OrderBy(a => a.Timestamp).ThenBy(a => a.Id).ToListAsync();
            string previous = string.Empty;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.EntryHash))
                {
                    entry.PreviousHash = previous;
                    entry.SchemaVersion = 1;
                    entry.EntryHash = ComputeHash(entry);
                }
                else if (!FixedEquals(entry.PreviousHash, previous) || !FixedEquals(entry.EntryHash, ComputeHash(entry)))
                {
                    throw new InvalidDataException($"La cadena de auditoría fue alterada en la entrada {entry.Id}.");
                }
                previous = entry.EntryHash;
            }
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER IF NOT EXISTS audit_logs_no_update BEFORE UPDATE ON audit_logs BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;");
            await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER IF NOT EXISTS audit_logs_no_delete BEFORE DELETE ON audit_logs BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;");
        }
        finally
        {
            AppendLock.Release();
        }
    }

    private string ComputeHash(AuditLogEntity entry)
    {
        string canonical = string.Join("\u001f", entry.SchemaVersion, entry.Id, entry.Timestamp, entry.UserId,
            entry.AuthorizedBySupervisorId ?? string.Empty, (int)entry.ActionType, (int)entry.RiskLevel,
            entry.EntityName, entry.EntityId, entry.OldValue, entry.NewValue,
            entry.FinancialImpact.ToString("G29", CultureInfo.InvariantCulture), entry.Reason,
            entry.TerminalName, entry.PreviousHash);
        return Convert.ToBase64String(HMACSHA256.HashData(_integrityKey, Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool FixedEquals(string left, string right)
    {
        byte[] a = Encoding.UTF8.GetBytes(left ?? string.Empty);
        byte[] b = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
