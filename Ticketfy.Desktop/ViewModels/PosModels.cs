using CommunityToolkit.Mvvm.ComponentModel;
using Ticketfy.Data.Dtos;
using System;
using System.Collections.Generic;

namespace Ticketfy.ViewModels;

public record CategoryChipDto(string Name, int Count, string DisplayName);

public partial class ParkedTicketModel : ObservableObject
{
    public string TicketId { get; init; } = string.Empty;
    public string CustomerName { get; init; } = "Público General";
    public DateTime ParkedAt { get; init; } = DateTime.Now;
    public decimal TotalAmount { get; init; }
    public List<CartItemDto> Lines { get; init; } = new();
}
