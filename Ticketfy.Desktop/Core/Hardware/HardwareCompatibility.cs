using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ticketfy.Core.Hardware;

public enum PosHardwareKind { ReceiptPrinter, CashDrawer, BarcodeScanner, Scale }
public enum HardwareTransport { WindowsSpooler, Serial, Tcp9100, UsbHid }
public enum HardwareCertificationStatus { ProtocolCompatible, LabValidated, ProductionCertified }

public sealed record HardwareProfile(
    string Id,
    PosHardwareKind Kind,
    HardwareTransport Transport,
    string Protocol,
    HardwareCertificationStatus Certification,
    string Notes);

public static class HardwareCompatibility
{
    public static IReadOnlyList<HardwareProfile> Matrix { get; } =
    [
        new("escpos-winspool", PosHardwareKind.ReceiptPrinter, HardwareTransport.WindowsSpooler, "ESC/POS", HardwareCertificationStatus.ProtocolCompatible, "58/80 mm; corte sujeto al modelo"),
        new("escpos-serial", PosHardwareKind.ReceiptPrinter, HardwareTransport.Serial, "ESC/POS", HardwareCertificationStatus.ProtocolCompatible, "8N1; baud configurable"),
        new("escpos-tcp9100", PosHardwareKind.ReceiptPrinter, HardwareTransport.Tcp9100, "ESC/POS RAW", HardwareCertificationStatus.ProtocolCompatible, "URI tcp://host:port; 9100 habitual"),
        new("drawer-rj11", PosHardwareKind.CashDrawer, HardwareTransport.Serial, "ESC p pin 2/5", HardwareCertificationStatus.ProtocolCompatible, "Conectado a impresora; no es puerto serial directo"),
        new("scanner-hid", PosHardwareKind.BarcodeScanner, HardwareTransport.UsbHid, "Keyboard wedge + CR/LF", HardwareCertificationStatus.ProtocolCompatible, "EAN/UPC/Code128 según lector"),
        new("scale-ascii", PosHardwareKind.Scale, HardwareTransport.Serial, "ASCII stable-weight line", HardwareCertificationStatus.ProtocolCompatible, "kg/g; 8N1; requiere trama estable")
    ];

    public static bool TryValidatePrinterTarget(string target, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(target)) { error = "Debe indicar la impresora."; return false; }
        target = target.Trim();
        if (target.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = target.Split('@', 2, StringSplitOptions.TrimEntries);
            if (!Regex.IsMatch(parts[0], @"^COM[1-9][0-9]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            { error = "Puerto serial de impresora inválido."; return false; }
            if (parts.Length == 2 && (!int.TryParse(parts[1], out int baud) || baud is < 1200 or > 115200))
            { error = "Baud de impresora fuera de rango."; return false; }
            return true;
        }
        if (target.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535)
            { error = "Endpoint TCP de impresora inválido."; return false; }
            return true;
        }
        if (target.Contains('/') || target.Contains('\\'))
        { error = "El nombre de impresora del sistema no puede ser una ruta."; return false; }
        return true;
    }
}

public static partial class ScaleReadingParser
{
    [GeneratedRegex(@"(?i)^(?:ST|STABLE)[,\s:]*(?:GS|NT)?[,\s:]*[+\-]?\s*(?<value>\d+(?:[\.,]\d+)?)\s*(?<unit>KG|KGS|G|GRAMS?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableWeightPattern();

    public static bool TryParseStableKilograms(string frame, out decimal kilograms)
    {
        kilograms = 0m;
        if (string.IsNullOrWhiteSpace(frame)) return false;
        Match match = StableWeightPattern().Match(frame.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            return false;
        kilograms = match.Groups["unit"].Value.StartsWith("G", StringComparison.OrdinalIgnoreCase)
            ? value / 1000m
            : value;
        return kilograms >= 0m;
    }
}

public sealed class BarcodeInputBuffer
{
    private readonly int _maxLength;
    private readonly System.Text.StringBuilder _buffer = new();

    public BarcodeInputBuffer(int maxLength = 128) => _maxLength = Math.Max(8, maxLength);

    public string? Push(char value)
    {
        if (value is '\r' or '\n')
        {
            string barcode = _buffer.ToString().Trim();
            _buffer.Clear();
            return barcode.Length == 0 ? null : barcode;
        }
        if (!char.IsControl(value) && _buffer.Length < _maxLength) _buffer.Append(value);
        return null;
    }

    public void Reset() => _buffer.Clear();
}
