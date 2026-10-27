using Microsoft.Data.Sqlite;

namespace Ticketfy.Core.Configuration;

/// <summary>One connection policy for the encrypted local database and WAL readers.</summary>
internal static class CommercialDatabaseConnection
{
    internal static string Create(string path, string password) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Password = password,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // SQLCipher connections must not share a codec cache. WAL provides reader/writer concurrency.
        Cache = SqliteCacheMode.Private
    }.ToString();
}
