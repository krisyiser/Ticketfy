using System;
using System.Threading;
using System.Threading.Tasks;
using Ticketfy.Core.Helpers;

namespace Ticketfy.Services.Interfaces;

public interface IScaleService
{
    Task<Result<decimal>> ReadStableKilogramsAsync(
        string portName,
        int baudRate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
