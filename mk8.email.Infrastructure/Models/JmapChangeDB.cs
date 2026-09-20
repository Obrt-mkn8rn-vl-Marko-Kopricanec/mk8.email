using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_changes")]
public sealed class JmapChangeDB
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("sequence")]
    public long Sequence { get; set; }

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Required]
    [MaxLength(32)]
    [Column("data_type")]
    public string DataType { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("object_id")]
    public string ObjectId { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    [Column("change_kind")]
    public string ChangeKind { get; set; } = string.Empty;

    [Column("changed_at")]
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
