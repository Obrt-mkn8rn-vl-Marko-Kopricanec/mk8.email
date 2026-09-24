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

    // Denormalized by the database from the owning address-book collection. This
    // is null for calendar resources and gives PostgreSQL a row-local key with
    // which to enforce the RFC 9610 account-wide ContactCard UID invariant.
    [Column("addressbook_user_id")]
    public Guid? AddressBookUserId { get; set; }

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

    [Column("content")]
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
