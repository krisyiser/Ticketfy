using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Operations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed partial class LocalOperationalTelemetry : IOperationalTelemetry
{
    private const long MaximumFileBytes = 1_048_576;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _utcNow;
    private TelemetryConsent _consent;

    public LocalOperationalTelemetry(
        TelemetryConsent consent = TelemetryConsent.Disabled,
        string? directory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _consent = consent;
        _directory = Path.GetFullPath(directory ?? AppDataPaths.TelemetryDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public TelemetryConsent Consent => _consent;

    public void SetConsent(TelemetryConsent consent)
    {
        _consent = consent;
        if (consent == TelemetryConsent.Disabled && Directory.Exists(_directory))
        {
            foreach (string path in Directory.EnumerateFiles(_directory, "telemetry-*.jsonl")) File.Delete(path);
        }
    }

    public Task TrackAsync(string eventName, bool succeeded, CancellationToken cancellationToken = default)
    {
        if (_consent != TelemetryConsent.AnonymousOperational) return Task.CompletedTask;
        if (!AllowedEventName().IsMatch(eventName))
            throw new ArgumentException("El nombre de telemetría sólo admite letras minúsculas, números, punto, guion y guion bajo.", nameof(eventName));

        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_directory);
                DeleteExpiredFiles();
                string path = GetWritableFile();
                string line = JsonSerializer.Serialize(new { timestampUtc = _utcNow(), eventName, succeeded });
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(ex, "Local operational telemetry could not be recorded");
            }
        }, cancellationToken);
    }

    private string GetWritableFile()
    {
        string prefix = $"telemetry-{_utcNow().UtcDateTime:yyyyMMdd}";
        for (int sequence = 0; sequence < 100; sequence++)
        {
            string path = Path.Combine(_directory, $"{prefix}-{sequence:D2}.jsonl");
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes) return path;
        }
        throw new IOException("Se alcanzó el límite diario de telemetría local.");
    }

    private void DeleteExpiredFiles()
    {
        DateTime cutoff = (_utcNow() - TimeSpan.FromDays(30)).UtcDateTime;
        foreach (FileInfo file in new DirectoryInfo(_directory).EnumerateFiles("telemetry-*.jsonl")
                     .Where(file => file.LastWriteTimeUtc < cutoff))
            file.Delete();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedEventName();
}
