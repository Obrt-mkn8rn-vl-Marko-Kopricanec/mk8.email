using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_blobs")]
public sealed class JmapBlobDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    [Column("blob_id")]
    public string BlobId { get; set; } = string.Empty;

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("content_type")]
    public string ContentType { get; set; } = "application/octet-stream";

    [MaxLength(255)]
    [Column("name")]
    public string? Name { get; set; }

    [Required]
    [Column("content", TypeName = "bytea")]
    public byte[] Content { get; set; } = [];

    [Column("size_bytes")]
    public long SizeBytes { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }
}
