using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_email_submissions")]
public sealed class JmapEmailSubmissionDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("submission_object_id")]
    public string SubmissionObjectId { get; set; } = string.Empty;

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("identity_id")]
    public string IdentityId { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    [Column("email_id")]
    public string EmailId { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    [Column("thread_id")]
    public string ThreadId { get; set; } = string.Empty;

    [Column("queue_id")]
    public Guid QueueId { get; set; }

    [Required]
    [MaxLength(320)]
    [Column("envelope_sender")]
    public string EnvelopeSender { get; set; } = string.Empty;

    [Column("envelope_recipients", TypeName = "text[]")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public string[] EnvelopeRecipients { get; set; } = [];

    [Column("envelope_json", TypeName = "jsonb")]
    public string? EnvelopeJson { get; set; }

    [Required]
    [MaxLength(16)]
    [Column("undo_status")]
    public string UndoStatus { get; set; } = "final";

    [Column("send_at")]
    public DateTime SendAt { get; set; } = DateTime.UtcNow;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
