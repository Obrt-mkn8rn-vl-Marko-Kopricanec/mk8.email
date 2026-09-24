using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("mfa_totp_credentials")]
public sealed class MfaTotpCredentialDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public UserDB User { get; set; } = null!;

    [Required]
    [MaxLength(128)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Column("encrypted_secret", TypeName = "bytea")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public byte[] EncryptedSecret { get; set; } = [];

    [Required]
    [Column("encryption_nonce", TypeName = "bytea")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public byte[] EncryptionNonce { get; set; } = [];

    [Required]
    [Column("encryption_tag", TypeName = "bytea")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public byte[] EncryptionTag { get; set; } = [];

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("verified_at")]
    public DateTime? VerifiedAt { get; set; }

    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    [Column("last_accepted_time_step")]
    public long? LastAcceptedTimeStep { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<MfaRecoveryCodeDB> RecoveryCodes { get; set; } = [];
}
