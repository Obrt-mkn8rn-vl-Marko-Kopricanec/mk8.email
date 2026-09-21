using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("dav_resources")]
public sealed class DavResourceDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("collection_id")]
    public Guid CollectionId { get; set; }

    [ForeignKey(nameof(CollectionId))]
    public DavCollectionDB Collection { get; set; } = null!;

    [Required]
    [MaxLength(255)]
    [Column("resource_name")]
    public string ResourceName { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("uid")]
    public string Uid { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("content_type")]
    public string ContentType { get; set; } = string.Empty;

    [Required]
    [Column("content")]
    public byte[] Content { get; set; } = [];

    [Required]
    [MaxLength(64)]
    [Column("etag")]
    public string Etag { get; set; } = string.Empty;

    [Column("size_bytes")]
    public int SizeBytes { get; set; }

    [Column("change_sequence")]
    public long ChangeSequence { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
