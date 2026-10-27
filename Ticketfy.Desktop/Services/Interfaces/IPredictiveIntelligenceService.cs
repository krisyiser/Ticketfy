using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Services.Interfaces;

public class PredictiveAlertDto
{
    public string ProductId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal DaysRemaining { get; set; }
}

public interface IPredictiveIntelligenceService
{
    // Obtiene el producto más vendido junto con el producto escaneado
    Task<ProductDto?> GetTopCorrelatedProductAsync(string sourceProductId, List<string> currentCartProductIds, CancellationToken cancellationToken = default);

    // Obtiene alertas de resurtido urgente (cadena de suministro)
    Task<List<PredictiveAlertDto>> GetUrgentRestockAlertsAsync(CancellationToken cancellationToken = default);
}
