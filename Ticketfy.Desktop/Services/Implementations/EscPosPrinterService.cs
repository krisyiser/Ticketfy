using System;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ticketfy.Services.Interfaces;
using Ticketfy.Desktop.Core.Helpers;
using Serilog;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// ESC/POS Thermal Printer hardware driver with background Channel queue.
/// Decomposed into partial classes: EscPosPrinterService (Core), EscPosPrinterService.Tickets, EscPosPrinterService.Reports.
/// </summary>
public partial class EscPosPrinterService : IEscPosPrinterService, IDisposable, IAsyncDisposable
{
    private static readonly byte[] EscInit = [0x1B, 0x40];
    private static readonly byte[] AlignLeft = [0x1B, 0x61, 0x00];
    private static readonly byte[] AlignCenter = [0x1B, 0x61, 0x01];
    private static readonly byte[] AlignRight = [0x1B, 0x61, 0x02];
    private static readonly byte[] BoldOn = [0x1B, 0x45, 0x01];
    private static readonly byte[] BoldOff = [0x1B, 0x45, 0x00];
    private static readonly byte[] DoubleSizeOn = [0x1D, 0x21, 0x11];
    private static readonly byte[] DoubleSizeOff = [0x1D, 0x21, 0x00];
    private static readonly byte[] PaperCut = [0x1D, 0x56, 0x42, 0x00];

    private static readonly byte[] DrawerPin2Command = [0x1B, 0x70, 0x00, 0x19, 0xFA];
    private static readonly byte[] DrawerPin5Command = [0x1B, 0x70, 0x01, 0x19, 0xFA];

    private record PrintJob(string PortOrName, byte[] Payload, TaskCompletionSource<bool> Completion);

    private readonly Channel<PrintJob> _printerQueue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processingTask;
    private readonly ISettingsService? _settings;

    public EscPosPrinterService(ISettingsService? settings = null)
    {
        _settings = settings;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _printerQueue = Channel.CreateUnbounded<PrintJob>();
        _cts = new CancellationTokenSource();
        _processingTask = ProcessPrintQueueAsync(_cts.Token);
    }

    public async Task<bool> OpenCashDrawerAsync(string printerPortOrName = "", int drawerPin = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(printerPortOrName) && _settings is not null)
            {
                var hardware = (await _settings.GetAppSettingsAsync()).Hardware;
                if (!hardware.EnableCashDrawerTrigger) return false;
                printerPortOrName = hardware.PrinterPort;
                drawerPin = hardware.CashDrawerPin;
            }
            if (drawerPin is not (0 or 1)) return false;
            byte[] command = drawerPin == 1 ? DrawerPin5Command : DrawerPin2Command;
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(command, 0, command.Length);

            return await EnqueuePrintJobAsync(printerPortOrName, ms.ToArray(), true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing open cash drawer pulse on pin {Pin}", drawerPin);
            return false;
        }
    }

    public Task<bool> IsPrinterOnlineAsync()
    {
        // Kept for interface compatibility. A write cannot establish physical readiness.
        return Task.FromResult(false);
    }

    private async Task<bool> EnqueuePrintJobAsync(string portOrName, byte[] payload, bool drawer = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(portOrName) && _settings != null)
                portOrName = (await _settings.GetAppSettingsAsync()).Hardware.PrinterPort.Trim();
            if (!Ticketfy.Core.Hardware.HardwareCompatibility.TryValidatePrinterTarget(portOrName, out _))
            {
                Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("No hay un destino de impresión válido. Configure Hardware & Tickets; la operación registrada no se repite.", true);
                return false;
            }
        }
        catch
        {
            Ticketfy.Core.Messages.HardwareFeedbackMessage.Report("No se pudo leer la configuración de impresión. No se envió el trabajo.", true);
            return false;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_printerQueue.Writer.TryWrite(new PrintJob(portOrName, payload, completion)))
            completion.TrySetResult(false);
        bool accepted = await completion.Task;
        string label = drawer ? "Pulso del cajón" : "Impresión";
        Ticketfy.Core.Messages.HardwareFeedbackMessage.Report(accepted
            ? $"{label}: enviado al controlador. Compruebe el resultado físico; no hay confirmación del dispositivo."
            : $"{label}: envío no confirmado. Revise conexión, papel y configuración antes de reintentar; el resultado físico puede ser incierto.", !accepted);
        return accepted;
    }

    private async Task ProcessPrintQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in _printerQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                    bool success = await SendRawBytesToHardwareAsync(job.PortOrName, job.Payload, timeoutCts.Token);
                    job.Completion.TrySetResult(success);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("Hardware Timeout: La impresora no respondió en 3 segundos.");
                    job.Completion.TrySetResult(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error writing raw bytes to hardware printer.");
                    job.Completion.TrySetResult(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (_printerQueue.Reader.TryRead(out var pending))
                pending.Completion.TrySetResult(false);
        }
    }

    private async Task<bool> SendRawBytesToHardwareAsync(string portOrName, byte[] bytes, CancellationToken token)
    {
        try
        {
            if (Uri.TryCreate(portOrName, UriKind.Absolute, out var endpoint) &&
                endpoint.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                int port = endpoint.Port > 0 ? endpoint.Port : 9100;
                using var client = new TcpClient();
                await client.ConnectAsync(endpoint.Host, port, token);
                await using NetworkStream stream = client.GetStream();
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                return true;
            }
            return await Task.Run(() =>
            {
                if (portOrName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string[] serialParts = portOrName.Split('@', 2, StringSplitOptions.TrimEntries);
                        int baudRate = serialParts.Length == 2 && int.TryParse(serialParts[1], out int parsedBaud)
                            ? parsedBaud
                            : 9600;
                        if (baudRate is < 1200 or > 115200) return false;
                        using var serial = new SerialPort(serialParts[0], baudRate, Parity.None, 8, StopBits.One);
                        serial.WriteTimeout = 2500;
                        serial.Open();
                        serial.Write(bytes, 0, bytes.Length);
                        serial.Close();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("Configured serial printer {Port} is not reachable: {Message}", portOrName, ex.Message);
                        return false;
                    }
                }
                bool success = RawPrinterHelper.SendBytesToPrinter(portOrName, bytes);
                if (!success) Log.Warning("Failed to send print job to Winspool printer {PrinterName}", portOrName);
                return success;
            }, token).WaitAsync(TimeSpan.FromSeconds(3), token);
        }
        catch (TimeoutException)
        {
            Log.Warning("Hardware Timeout: P/Invoke to winspool.drv hung for more than 3 seconds. Aborting await.");
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Hardware Canceled: Printer task was canceled.");
            return false;
        }
    }

    private static void WriteEscPosQrCode(Stream s, string qrData)
    {
        byte[] dataBytes = Encoding.UTF8.GetBytes(qrData);
        int storeLen = dataBytes.Length + 3;
        byte pL = (byte)(storeLen & 0xFF);
        byte pH = (byte)((storeLen >> 8) & 0xFF);

        s.Write([0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00], 0, 9);
        s.Write([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06], 0, 8);
        s.Write([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x30], 0, 8);
        s.Write([0x1D, 0x28, 0x6B, pL, pH, 0x31, 0x50, 0x30], 0, 8);
        s.Write(dataBytes, 0, dataBytes.Length);
        s.Write([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30], 0, 8);
    }

    private static void WriteString(Stream s, string text)
    {
        var encoding = Encoding.GetEncoding(858);
        byte[] bytes = encoding.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _printerQueue.Writer.Complete();
            _cts.Dispose();
        }
        catch
        {
            // Ignored on dispose
        }
    }

    public async ValueTask DisposeAsync()
    {
        _printerQueue.Writer.Complete();

        try
        {
            using var gracePeriodCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _printerQueue.Reader.Completion.WaitAsync(gracePeriodCts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning("Printer queue graceful shutdown timed out or failed: {Message}", ex.Message);
        }
        finally
        {
            _cts.Cancel();
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation
            }
            catch (Exception ex)
            {
                Log.Warning("Error waiting for printer worker task shutdown: {Message}", ex.Message);
            }
            _cts.Dispose();
        }
    }
}
