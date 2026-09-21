using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("oauth_tokens")]
public sealed class OAuthTokenDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("grant_id")]
    public Guid GrantId { get; set; }

    [ForeignKey(nameof(GrantId))]
    public OAuthGrantDB Grant { get; set; } = null!;

    [Required]
    [MaxLength(16)]
    [Column("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [Required]
    [Column("token_hash", TypeName = "bytea")]
    public byte[] TokenHash { get; set; } = [];

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("replaced_by_token_id")]
    public Guid? ReplacedByTokenId { get; set; }
}
