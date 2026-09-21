using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("dav_collections")]
public sealed class DavCollectionDB
{
    public const string CalendarType = "calendar";
    public const string AddressBookType = "addressbook";

    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public UserDB User { get; set; } = null!;

    [Required]
    [MaxLength(16)]
    [Column("collection_type")]
    public string CollectionType { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    [Column("slug")]
    public string Slug { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [Column("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(1024)]
    [Column("description")]
    public string? Description { get; set; }

    [MaxLength(32)]
    [Column("color")]
    public string? Color { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("components", TypeName = "text[]")]
    public string[] Components { get; set; } = [];

    [Column("sync_token")]
    public long SyncToken { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DavResourceDB> Resources { get; set; } = [];
    public ICollection<DavChangeDB> Changes { get; set; } = [];
    public ICollection<DavShareDB> Shares { get; set; } = [];
}
