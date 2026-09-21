using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("mail_queue_messages")]
public sealed class MailQueueMessageDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(320)]
    [Column("envelope_sender")]
    public string EnvelopeSender { get; set; } = string.Empty;

    [Required]
    [Column("raw_message")]
    public string RawMessage { get; set; } = string.Empty;

    [Column("requires_smtp_utf8")]
    public bool RequiresSmtpUtf8 { get; set; }

    [MaxLength(45)]
    [Column("client_ip")]
    public string? ClientIp { get; set; }

    [MaxLength(255)]
    [Column("helo")]
    public string? Helo { get; set; }

    [MaxLength(320)]
    [Column("authenticated_user")]
    public string? AuthenticatedUser { get; set; }

    [Required]
    [MaxLength(16)]
    [Column("direction")]
    public string Direction { get; set; } = string.Empty;

    [Required]
    [MaxLength(24)]
    [Column("state")]
    public string State { get; set; } = string.Empty;

    [Required]
    [MaxLength(24)]
    [Column("scan_state")]
    public string ScanState { get; set; } = string.Empty;

    [MaxLength(32)]
    [Column("scan_action")]
    public string? ScanAction { get; set; }

    [Column("scan_score")]
    public double? ScanScore { get; set; }

    [Column("added_headers")]
    public string? AddedHeaders { get; set; }

    [MaxLength(32)]
    [Column("target_folder")]
    public string? TargetFolder { get; set; }

    [Column("attempt_count")]
    public int AttemptCount { get; set; }

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    [Column("next_attempt_at")]
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    [Column("lease_token")]
    public Guid? LeaseToken { get; set; }

    [Column("lease_expires_at")]
    public DateTime? LeaseExpiresAt { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    [Column("sent_copy_created")]
    public bool SentCopyCreated { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    public ICollection<MailQueueRecipientDB> Recipients { get; set; } = [];
}
