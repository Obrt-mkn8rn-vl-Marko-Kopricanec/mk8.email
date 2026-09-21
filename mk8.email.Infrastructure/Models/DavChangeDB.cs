using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("dav_changes")]
public sealed class DavChangeDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("collection_id")]
    public Guid CollectionId { get; set; }

    [ForeignKey(nameof(CollectionId))]
    public DavCollectionDB Collection { get; set; } = null!;

    [Column("sequence")]
    public long Sequence { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("resource_name")]
    public string ResourceName { get; set; } = string.Empty;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [MaxLength(64)]
    [Column("etag")]
    public string? Etag { get; set; }

    [Column("changed_at")]
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
