using System;

namespace Ticketfy.Core.Operations;

public sealed record BackupPolicy(
    bool Enabled,
    TimeSpan Interval,
    int RetentionCount,
    TimeSpan RetentionAge,
    bool VerifyAfterCreation)
{
    public static BackupPolicy Default { get; } = new(
        Enabled: true,
        Interval: TimeSpan.FromHours(24),
        RetentionCount: 30,
        RetentionAge: TimeSpan.FromDays(30),
        VerifyAfterCreation: true);
}

public sealed record BackupValidationResult(
    bool IsValid,
    string Message,
    string? Sha256 = null,
    long SizeBytes = 0);

public sealed record RestorePreparationResult(
    bool IsReady,
    string Message,
    string? PendingRestorePath = null,
    string? SafetyBackupPath = null);

public sealed record BackupMaintenanceResult(
    bool Executed,
    bool Succeeded,
    string Message,
    string? BackupPath = null,
    int DeletedBackups = 0);

public sealed record DiagnosticExportResult(string ArchivePath, string Sha256);

public enum TelemetryConsent
{
    Disabled = 0,
    AnonymousOperational = 1
}
