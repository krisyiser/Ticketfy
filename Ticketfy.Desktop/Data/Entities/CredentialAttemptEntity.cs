using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

[Table("credential_attempts")]
public sealed class CredentialAttemptEntity
{
    [Key] public string Subject { get; set; } = "";
    public int Failures { get; set; }
    public long LastAttemptUtcTicks { get; set; }
    public long LockedUntilUtcTicks { get; set; }
}
