using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("mfa_recovery_codes")]
public sealed class MfaRecoveryCodeDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("credential_id")]
    public Guid CredentialId { get; set; }

    [ForeignKey(nameof(CredentialId))]
    public MfaTotpCredentialDB Credential { get; set; } = null!;

    [Required]
    [Column("code_hash", TypeName = "bytea")]
    public byte[] CodeHash { get; set; } = [];

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("used_at")]
    public DateTime? UsedAt { get; set; }
}
