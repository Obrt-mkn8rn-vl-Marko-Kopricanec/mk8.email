using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_push_subscriptions")]
public sealed class JmapPushSubscriptionDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("subscription_object_id")]
    public string SubscriptionObjectId { get; set; } = string.Empty;

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("device_client_id")]
    public string DeviceClientId { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    [Column("url")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056", Justification = "The EF-mapped URI text column and existing application contract require string.")]
    public string Url { get; set; } = string.Empty;

    [Column("types", TypeName = "text[]")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public string[]? Types { get; set; }

    [Column("keys_json", TypeName = "jsonb")]
    public string? KeysJson { get; set; }

    [Required]
    [MaxLength(128)]
    [Column("verification_code")]
    public string VerificationCode { get; set; } = string.Empty;

    [Column("is_verified")]
    public bool IsVerified { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Column("last_pushed_change")]
    public long LastPushedChange { get; set; }

    [Column("next_push_at")]
    public DateTime? NextPushAt { get; set; }

    [Column("failure_count")]
    public int FailureCount { get; set; }
}
