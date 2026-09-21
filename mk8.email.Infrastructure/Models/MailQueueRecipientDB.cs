using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("mail_queue_recipients")]
public sealed class MailQueueRecipientDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("message_id")]
    public Guid MessageId { get; set; }

    [ForeignKey(nameof(MessageId))]
    public MailQueueMessageDB Message { get; set; } = null!;

    [Required]
    [MaxLength(320)]
    [Column("recipient")]
    public string Recipient { get; set; } = string.Empty;

    [Column("is_local")]
    public bool IsLocal { get; set; }

    [Required]
    [MaxLength(24)]
    [Column("state")]
    public string State { get; set; } = string.Empty;

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("next_attempt_at")]
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    [Column("last_attempt_at")]
    public DateTime? LastAttemptAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("failure_notice_created")]
    public bool FailureNoticeCreated { get; set; }

    [Column("redirect_depth")]
    public int RedirectDepth { get; set; }

    [Column("redirect_history", TypeName = "text[]")]
    public string[] RedirectHistory { get; set; } = [];

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }
}
