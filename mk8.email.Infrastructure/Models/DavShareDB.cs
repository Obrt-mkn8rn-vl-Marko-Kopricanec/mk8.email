using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("dav_shares")]
public sealed class DavShareDB
{
    public const string ReadOnlyAccess = "read";
    public const string ReadWriteAccess = "read-write";

    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("collection_id")]
    public Guid CollectionId { get; set; }

    [ForeignKey(nameof(CollectionId))]
    public DavCollectionDB Collection { get; set; } = null!;

    [Column("grantee_user_id")]
    public Guid GranteeUserId { get; set; }

    [ForeignKey(nameof(GranteeUserId))]
    public UserDB GranteeUser { get; set; } = null!;

    [Required]
    [MaxLength(16)]
    [Column("access_level")]
    public string AccessLevel { get; set; } = ReadOnlyAccess;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
