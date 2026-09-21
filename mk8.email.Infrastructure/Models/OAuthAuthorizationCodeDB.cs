using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("oauth_authorization_codes")]
public sealed class OAuthAuthorizationCodeDB
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
    [Column("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    [Column("redirect_uri")]
    public string RedirectUri { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    [Column("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [Column("scopes", TypeName = "text[]")]
    public string[] Scopes { get; set; } = [];

    [Required]
    [MaxLength(128)]
    [Column("code_challenge")]
    public string CodeChallenge { get; set; } = string.Empty;

    [Required]
    [Column("code_hash", TypeName = "bytea")]
    public byte[] CodeHash { get; set; } = [];

    [MaxLength(512)]
    [Column("nonce")]
    public string? Nonce { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("consumed_at")]
    public DateTime? ConsumedAt { get; set; }
}
