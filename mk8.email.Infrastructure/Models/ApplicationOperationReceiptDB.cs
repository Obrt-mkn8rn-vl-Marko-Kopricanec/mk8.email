using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("application_operation_receipts")]
public sealed class ApplicationOperationReceiptDB
{
    [Key, Column("id")]
    public Guid Id { get; set; }

    [Column("operation_id")]
    public Guid OperationId { get; set; }

    [Column("step_number")]
    public int StepNumber { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Required, MaxLength(64), Column("purpose")]
    public string Purpose { get; set; } = string.Empty;

    [Required, MaxLength(32), Column("payload_object_provider")]
    public string ObjectProvider { get; set; } = string.Empty;

    [Required, MaxLength(1024), Column("payload_object_name")]
    public string ObjectName { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("payload_object_sha256")]
    public string ObjectSha256 { get; set; } = string.Empty;

    [Required, MaxLength(256), Column("payload_object_etag")]
    public string ObjectEntityTag { get; set; } = string.Empty;

    [Column("payload_length")]
    public long PayloadLength { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("effects_pending")]
    public bool EffectsPending { get; set; }

    [Column("effects_retry_at")]
    public DateTime EffectsRetryAt { get; set; }
}
