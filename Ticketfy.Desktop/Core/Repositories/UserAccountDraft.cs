namespace Ticketfy.Core.Repositories;

/// <summary>Account input, not a database entity. Password and PIN are literal secrets; never log this object.</summary>
public sealed class UserAccountDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Username { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Role { get; init; } = "CAJERO";
    public string Password { get; init; } = "";
    public string? Pin { get; init; }
    public bool IsActive { get; init; } = true;
}
