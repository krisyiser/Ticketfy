using System.Globalization;

namespace Ticketfy.Core.Models;

/// <summary>Physical pieces, never fractional currency amounts or silently coerced input.</summary>
public sealed record CashDenominationCount(int Thousand, int FiveHundred, int TwoHundred, int Hundred,
    int Fifty, int Twenty, int Ten, int Five, int Two, int One, int Half)
{
    public int[] Pieces => [Thousand, FiveHundred, TwoHundred, Hundred, Fifty, Twenty, Ten, Five, Two, One, Half];
    public decimal Total => Thousand * 1000m + FiveHundred * 500m + TwoHundred * 200m + Hundred * 100m
        + Fifty * 50m + Twenty * 20m + Ten * 10m + Five * 5m + Two * 2m + One + Half * .50m;
    public bool HasPieces => Pieces.Any(p => p != 0);
    public bool IsConsistentWith(decimal declared) => Pieces.All(p => p >= 0) && declared >= 0m
        && declared <= long.MaxValue / 100m && decimal.Round(declared, 2) == declared
        && (!HasPieces || Total == declared);

    public static bool TryParse(IReadOnlyList<string?> inputs, out CashDenominationCount? count)
    {
        count = null;
        if (inputs.Count != 11) return false;
        var pieces = new int[11];
        for (int i = 0; i < pieces.Length; i++)
        {
            string text = inputs[i]?.Trim() ?? "";
            if (text.Length == 0) continue; // A cleared field explicitly represents no pieces.
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out pieces[i])) return false;
        }
        count = new(pieces[0], pieces[1], pieces[2], pieces[3], pieces[4], pieces[5],
            pieces[6], pieces[7], pieces[8], pieces[9], pieces[10]);
        return true;
    }
}
