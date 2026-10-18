using System;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Hardware;
using Ticketfy.Core.Helpers;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public sealed class SerialScaleService : IScaleService
{
    public async Task<Result<decimal>> ReadStableKilogramsAsync(
        string portName,
        int baudRate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase))
            return Result<decimal>.Failure("El puerto de la báscula no está disponible.");
        if (baudRate is < 1200 or > 115200)
            return Result<decimal>.Failure("Velocidad serial de báscula no soportada.");

        try
        {
            using var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                NewLine = "\r\n",
                ReadTimeout = Math.Max(100, (int)Math.Min(timeout.TotalMilliseconds, 5000))
            };
            port.Open();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            while (!linked.IsCancellationRequested)
            {
                string frame = await Task.Run(port.ReadLine, linked.Token);
                if (ScaleReadingParser.TryParseStableKilograms(frame, out decimal kilograms))
                    return Result<decimal>.Success(kilograms);
            }
            return Result<decimal>.Failure("La báscula no entregó una lectura estable.");
        }
        catch (OperationCanceledException)
        {
            return Result<decimal>.Failure("La lectura de la báscula agotó el tiempo de espera.");
        }
        catch (TimeoutException)
        {
            return Result<decimal>.Failure("La báscula no respondió.");
        }
        catch (Exception)
        {
            return Result<decimal>.Failure("No se pudo leer la báscula configurada.");
        }
    }
}
