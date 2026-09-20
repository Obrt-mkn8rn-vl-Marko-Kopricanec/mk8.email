using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_identities")]
public sealed class JmapIdentityDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("identity_object_id")]
    public string IdentityObjectId { get; set; } = string.Empty;

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(320)]
    [Column("email")]
    public string Email { get; set; } = string.Empty;

    [Column("reply_to_json", TypeName = "jsonb")]
    public string? ReplyToJson { get; set; }

    [Column("bcc_json", TypeName = "jsonb")]
    public string? BccJson { get; set; }

    [Required]
    [Column("text_signature")]
    public string TextSignature { get; set; } = string.Empty;

    [Required]
    [Column("html_signature")]
    public string HtmlSignature { get; set; } = string.Empty;

    [Column("may_delete")]
    public bool MayDelete { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
