using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Services.Security;

/// <summary>Process-local trust, scoped to one application session. Only the credential verifier can issue it.</summary>
public sealed class QuickAccessSession(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, Lease> _leases = new();
    private long _epoch;
    private sealed record Lease(string Fingerprint, long Issued, long Observed);
    private sealed record DatabaseInstance(string Id);
    private static readonly ConditionalWeakTable<DbConnection, DatabaseInstance> DatabaseInstances = new();
    internal long Epoch { get { lock (_sync) return _epoch; } }

    public void Clear()
    {
        lock (_sync) { _leases.Clear(); ++_epoch; }
    }

    internal static string Subject(AppDbContext db, UserEntity user)
    {
        var connection = db.Database.GetDbConnection();
        // Do not retain or log a connection string (it may contain a DB key).
        string scope = connection.ConnectionString + "\n" + connection.DataSource;
        if (connection.DataSource is ":memory:" or "")
            scope += "\n" + DatabaseInstances.GetValue(connection, _ => new(Guid.NewGuid().ToString("N"))).Id;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ":" + user.Id.ToString("N");
    }
    internal static string Fingerprint(UserEntity user) => CommandIdentity.Hash(user.Id, user.Username,
        user.PasswordHash, user.PinCode, user.Role, user.RoleString, user.IsActive, user.PasswordPolicyVersion);

    internal void Issue(long epoch, string subject, string fingerprint)
    {
        lock (_sync)
        {
            if (epoch != _epoch) return;
            long now = _clock.GetUtcNow().UtcTicks;
            foreach (var key in _leases.Where(p => !ValidTime(p.Value, now)).Select(p => p.Key).ToArray()) _leases.Remove(key);
            // A busy shared terminal must not retain an unbounded directory of authenticated people.
            if (_leases.Count >= 256 && !_leases.ContainsKey(subject))
                _leases.Remove(_leases.MinBy(p => p.Value.Issued).Key);
            _leases[subject] = new(fingerprint, now, now);
        }
    }
    internal bool Allows(string subject, string fingerprint)
    {
        lock (_sync)
        {
            long now = _clock.GetUtcNow().UtcTicks;
            if (!_leases.TryGetValue(subject, out var lease)) return false;
            if (lease.Fingerprint != fingerprint || !ValidTime(lease, now))
            { _leases.Remove(subject); return false; }
            _leases[subject] = lease with { Observed = now };
            return true;
        }
    }
    internal void Rehash(long epoch, string subject, string before, string after)
    {
        lock (_sync)
        {
            if (epoch == _epoch && _leases.TryGetValue(subject, out var lease) && lease.Fingerprint == before)
                _leases[subject] = lease with { Fingerprint = after }; // Never renew the primary-authentication deadline.
        }
    }
    private static bool ValidTime(Lease lease, long now) =>
        now >= lease.Observed && now >= lease.Issued && now - lease.Issued < Lifetime.Ticks;
}
