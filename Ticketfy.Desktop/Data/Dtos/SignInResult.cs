namespace Ticketfy.Data.Dtos;

// A valid weak password proves the right to rotate itself, not the right to open a business session.
public sealed record SignInResult(UserDto? User = null, bool PasswordChangeRequired = false);
