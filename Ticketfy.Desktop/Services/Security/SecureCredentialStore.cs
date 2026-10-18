using System.Security.Cryptography;
using System.Text;
using Ticketfy.Core.Configuration;

namespace Ticketfy.Services.Security;

/// <summary>Named credentials encrypted with Windows DPAPI for the current user.</summary>
public sealed class SecureCredentialStore
{
    private readonly string _directory;
    public SecureCredentialStore(string? directory = null) => _directory = directory ?? AppDataPaths.CredentialDirectory;

    public string? Read(string name)
    {
        var store = new DpapiSecretStore(PathFor(name));
        return store.Exists ? store.Read() : null;
    }

    public void Write(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("La credencial no puede estar vacía.", nameof(value));
        new DpapiSecretStore(PathFor(name)).Write(value);
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    private string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nombre de credencial inválido.", nameof(name));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToUpperInvariant())));
        return Path.Combine(_directory, digest + ".dpapi");
    }
}
