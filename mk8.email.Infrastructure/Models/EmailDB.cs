using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("emails")]
public class EmailDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("sender")]
    public string Sender { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("recipient")]
    public string Recipient { get; set; } = string.Empty;

    [Required]
    [MaxLength(998)]
    [Column("subject")]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [Column("body")]
    public string Body { get; set; } = string.Empty;

    [Column("is_read")]
    public bool IsRead { get; set; }

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("is_flagged")]
    public bool IsFlagged { get; set; }

    [Column("is_draft")]
    public bool IsDraft { get; set; }

    [Column("is_answered")]
    public bool IsAnswered { get; set; }

    [Column("keywords", TypeName = "text[]")]
    public string[] Keywords { get; set; } = [];

    [Column("mod_seq")]
    public long ModSeq { get; set; }

    [Column("uid")]
    public int Uid { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("email_object_id")]
    public string EmailObjectId { get; set; } = Guid.CreateVersion7().ToString("N");

    [MaxLength(64)]
    [Column("thread_object_id")]
    public string? ThreadObjectId { get; set; }

    [Column("size_bytes")]
    public int SizeBytes { get; set; }

    [Column("raw_headers")]
    public string? RawHeaders { get; set; }

    // Preserve the exact RFC 5322 octets. RawHeaders/Body remain populated for
    // efficient legacy IMAP searches, but cannot losslessly represent the
    // original line endings or a message without a header/body separator.
    [Column("raw_message")]
    public byte[]? RawMessage { get; set; }

    [MaxLength(255)]
    [Column("message_id")]
    public string? MessageId { get; set; }

    [Column("in_reply_to")]
    public string? InReplyTo { get; set; }

    [Column("cc")]
    public string? Cc { get; set; }

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    [Column("queue_delivery_id")]
    public Guid? QueueDeliveryId { get; set; }

    [Column("folder_id")]
    public Guid FolderId { get; set; }

    [ForeignKey(nameof(FolderId))]
    public FolderDB Folder { get; set; } = null!;
}
