using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>Durable cursor over immutable sale lines. Pair counters and cursor commit together.</summary>
[Table("recommendation_jobs")]
public sealed class RecommendationJobEntity
{
    [Key] public long Id { get; set; }
    public string SaleId { get; set; } = "";
    public int NextLeft { get; set; }
    public int NextRight { get; set; } = 1;
    public bool IsCompleted { get; set; }
}
