using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Stores only webhook routing metadata and a payload hash. Duplicate deliveries
/// are acknowledged once and reconciled later by querying the provider API.
/// </summary>
public sealed class IntegrationWebhookInbox
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public IntegrationWebhookInbox(IDbContextFactory<AppDbContext> contextFactory) => _contextFactory = contextFactory;

    public async Task<bool> TryAcceptAsync(
        string provider,
        string eventId,
        string resourceId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("El webhook no contiene identificadores válidos.");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.IntegrationWebhookEvents.AnyAsync(e => e.Provider == provider && e.EventId == eventId, cancellationToken))
            return false;
        context.IntegrationWebhookEvents.Add(new IntegrationWebhookEventEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = provider,
            EventId = eventId,
            ResourceId = resourceId,
            PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ReceivedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
