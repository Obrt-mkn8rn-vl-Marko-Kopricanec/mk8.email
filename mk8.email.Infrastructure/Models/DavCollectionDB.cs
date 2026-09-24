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

    [Column("is_default")]
    public bool IsDefault { get; set; }

    [Column("is_subscribed")]
    public bool IsSubscribed { get; set; } = true;

    [Column("components", TypeName = "text[]")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1819", Justification = "EF-mapped array column preserves the relational schema and existing persistence contract.")]
    public string[] Components { get; set; } = [];

    [Column("sync_token")]
    public long SyncToken { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<DavResourceDB> Resources { get; set; } = [];
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<DavChangeDB> Changes { get; set; } = [];
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<DavShareDB> Shares { get; set; } = [];
}
