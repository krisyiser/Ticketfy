using System.Security.Cryptography;
using System.Text;

namespace Ticketfy.Services.Security;

/// <summary>Offline, immutable screening data. Digests describe public examples, never account credentials.</summary>
internal sealed class PasswordBlocklist
{
    internal const int Version = 2;
    internal const int PackagedEntries = 97746;
    internal const string PackagedSha256 = "3C36D23FA8D5A5AE79F1AA6188B8BFE4B4E18EF4E23DB11F37EC47338BA8989A";
    private const string ResourceName = "Ticketfy.Security.CommonPasswordsV2";
    private const string InvalidData = "La lista local de contraseñas no se pudo verificar. Reinstale un paquete verificado; no desactive la protección.";
    private readonly byte[] _digests;
    private PasswordBlocklist(byte[] digests) => _digests = digests;
    private static readonly Lazy<PasswordBlocklist> Packaged = new(() =>
    {
        using var stream = typeof(PasswordBlocklist).Assembly.GetManifestResourceStream(ResourceName);
        return Read(stream, PackagedEntries, PackagedSha256);
    });
    internal static PasswordBlocklist Current => Packaged.Value;
    internal int Count => _digests.Length / SHA256.HashSizeInBytes;
    internal static void VerifyIntegrity() => _ = Current;

    internal bool Contains(string secret)
    {
        // This normalization is for screening only. Password verification keeps the literal input.
        string candidate = secret.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate), digest);
        int left = 0, right = Count - 1;
        while (left <= right)
        {
            int middle = left + (right - left) / 2;
            int comparison = digest.SequenceCompareTo(_digests.AsSpan(middle * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes));
            if (comparison == 0) return true;
            if (comparison < 0) right = middle - 1;
            else left = middle + 1;
        }
        return false;
    }

    internal static PasswordBlocklist Read(Stream? stream, int expectedEntries, string expectedSha256)
    {
        // Check size before hashing/allocating; a missing/corrupt resource never becomes an empty list.
        if (stream is null || !stream.CanSeek || !stream.CanRead || expectedEntries is <= 0 or > 100000
            || stream.Length != expectedEntries * 65L) throw new InvalidOperationException(InvalidData);
        stream.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(InvalidData);
        stream.Position = 0;
        var digests = new byte[expectedEntries * SHA256.HashSizeInBytes];
        Span<byte> line = stackalloc byte[65];
        for (int index = 0; index < expectedEntries; index++)
        {
            stream.ReadExactly(line);
            if (line[64] != '\n') throw new InvalidOperationException(InvalidData);
            for (int column = 0; column < 64; column++)
                if (line[column] is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'A' and <= (byte)'F'))
                    throw new InvalidOperationException(InvalidData);
            byte[] digest = Convert.FromHexString(Encoding.ASCII.GetString(line[..64]));
            if (index > 0 && digests.AsSpan((index - 1) * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes).SequenceCompareTo(digest) >= 0)
                throw new InvalidOperationException(InvalidData);
            digest.CopyTo(digests, index * SHA256.HashSizeInBytes);
        }
        return new PasswordBlocklist(digests);
    }
}
