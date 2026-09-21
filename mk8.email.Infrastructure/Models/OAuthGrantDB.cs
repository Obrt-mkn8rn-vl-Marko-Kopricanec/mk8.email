using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("oauth_grants")]
public sealed class OAuthGrantDB
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
    [MaxLength(128)]
    [Column("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [Column("scopes", TypeName = "text[]")]
    public string[] Scopes { get; set; } = [];

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    public ICollection<OAuthTokenDB> Tokens { get; set; } = [];
}
