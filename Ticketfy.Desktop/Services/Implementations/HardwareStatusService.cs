using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Ticketfy.Core.Models.Settings;

namespace Ticketfy.Services.Implementations;

public sealed record PrinterQueueStatus(string Name, bool Unavailable);
public sealed record HardwareInventory(IReadOnlyList<PrinterQueueStatus> Printers, IReadOnlyList<string> SerialPorts,
    IReadOnlyList<string> Keyboards, string Warning = "");
public sealed record PeripheralStatus(string Device, string State, string Detail, bool NeedsAttention);

public interface IHardwareInventoryReader
{
    Task<HardwareInventory> ReadAsync();
}

/// <summary>Passive OS inventory only: never prints, opens a drawer, claims a serial port or contacts a payment terminal.</summary>
public sealed class WindowsHardwareInventoryReader : IHardwareInventoryReader
{
    public Task<HardwareInventory> ReadAsync() => Task.Run(() =>
    {
        var printers = new List<PrinterQueueStatus>();
        var keyboards = new List<string>();
        string[] ports = [];
        var warnings = new List<string>();
        if (!OperatingSystem.IsWindows()) return new HardwareInventory([], [], [], "Detección disponible únicamente en Windows.");
        try { ports = SerialPort.GetPortNames(); }
        catch { warnings.Add("No se pudieron consultar los puertos seriales."); }
        try
        {
            using var searcher = Query("SELECT Name, WorkOffline, PrinterStatus FROM Win32_Printer");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                    printers.Add(new(Convert.ToString(item["Name"]) ?? "", Convert.ToBoolean(item["WorkOffline"] ?? false) ||
                        Convert.ToInt32(item["PrinterStatus"] ?? 0) is 6 or 7));
            }
        }
        catch { warnings.Add("No se pudieron consultar las colas de impresión."); }
        try
        {
            using var searcher = Query("SELECT Name FROM Win32_Keyboard");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
                using (item) keyboards.Add(Convert.ToString(item["Name"]) ?? "Teclado HID");
        }
        catch { warnings.Add("No se pudieron consultar los dispositivos de teclado."); }
        return new HardwareInventory(printers, ports, keyboards, string.Join(" ", warnings));
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ManagementObjectSearcher Query(string query) => new(new ManagementScope(@"root\CIMV2"),
        new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false });
}

public static class HardwareStatusService
{
    public static IReadOnlyList<PeripheralStatus> Evaluate(HardwareConfig config, HardwareInventory inventory)
    {
        var statuses = new List<PeripheralStatus>();
        string printer = config.PrinterPort.Trim();
        if (printer.Length == 0) statuses.Add(new("Impresora", "Sin configurar", "Configure el destino en Ajustes > Hardware & Tickets.", true));
        else if (printer.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            statuses.Add(new("Impresora", "Configurada; sin comprobar", "Destino TCP configurado. Este monitor no realiza conexiones ni envía trabajos de prueba.", true));
        else if (printer.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) statuses.Add(Serial("Impresora", printer, inventory));
        else
        {
            var queue = inventory.Printers.FirstOrDefault(p => p.Name.Equals(printer, StringComparison.OrdinalIgnoreCase));
            statuses.Add(new("Impresora", queue is null ? "Cola no detectada" : queue.Unavailable ? "Cola no disponible" : "Cola detectada",
                $"{printer}. Una cola instalada no confirma conexión, papel ni impresión física.", queue is null || queue.Unavailable));
        }
        statuses.Add(new("Cajón", config.EnableCashDrawerTrigger ? "Configurado; sin sensor" : "Apertura automática desactivada",
            "El cajón RJ11 depende de la impresora. No es posible detectar su conexión o apertura sin un sensor compatible.", false));
        statuses.Add(new("Lector", "Pendiente de prueba de entrada",
            $"Modo teclado HID + Enter. Windows informa {inventory.Keyboards.Count} dispositivo(s) de teclado, pero no distingue con certeza el lector. Use la prueba de entrada; no registra ventas.", false));
        statuses.Add(string.IsNullOrWhiteSpace(config.ScalePort)
            ? new("Báscula", "Sin configurar", "Requiere puerto y una lectura estable válida; detectar un puerto no identifica la báscula.", false)
            : Serial("Báscula", config.ScalePort, inventory));
        if (!string.IsNullOrWhiteSpace(inventory.Warning)) statuses.Add(new("Detección", "Consulta incompleta", inventory.Warning, true));
        return statuses;
    }

    private static PeripheralStatus Serial(string device, string configured, HardwareInventory inventory)
    {
        string port = configured.Split('@', 2)[0].Trim();
        bool found = inventory.SerialPorts.Contains(port, StringComparer.OrdinalIgnoreCase);
        return new(device, found ? "Puerto detectado" : "Puerto no detectado",
            $"{port}. La presencia del puerto no confirma el dispositivo ni su protocolo.", !found);
    }
}
