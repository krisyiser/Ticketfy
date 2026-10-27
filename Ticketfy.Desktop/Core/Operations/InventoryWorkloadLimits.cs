using System;
using System.IO;
using System.Threading.Tasks;

namespace Ticketfy.Core.Operations;

/// <summary>Explicit local-operation budgets, not silent truncation or a certified capacity claim.</summary>
public static class InventoryWorkloadLimits
{
    public const int ImportRows = 1000;
    public const int CsvCharacters = 2 * 1024 * 1024;
    public const int PrintableRows = 500;
    public const int SnapshotRows = 100000;
    public const int SnapshotBatch = 500;

    public static async Task<string> ReadCsvAsync(TextReader reader)
    {
        var buffer = new char[CsvCharacters + 1];
        int count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        if (count > CsvCharacters) throw new InvalidOperationException("El CSV supera 2 millones de caracteres. Divida la importación en archivos más pequeños.");
        return new string(buffer, 0, count);
    }
}
