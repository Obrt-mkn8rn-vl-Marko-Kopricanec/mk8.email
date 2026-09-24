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

    [Column("content", TypeName = "bytea")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public byte[]? Content { get; set; }

    [MaxLength(32)]
    [Column("object_provider")]
    public string? ObjectProvider { get; set; }

    [MaxLength(1024)]
    [Column("object_name")]
    public string? ObjectName { get; set; }

    [MaxLength(64)]
    [Column("object_sha256")]
    public string? ObjectSha256 { get; set; }

    [MaxLength(256)]
    [Column("object_etag")]
    public string? ObjectEntityTag { get; set; }

    [Column("size_bytes")]
    public long SizeBytes { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }
}
