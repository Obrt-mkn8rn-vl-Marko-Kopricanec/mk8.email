using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("inboxes")]
public class InboxDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("address_id")]
    public Guid AddressId { get; set; }

    [ForeignKey(nameof(AddressId))]
    public AddressDB Address { get; set; } = null!;

    [Column("owner_id")]
    public Guid OwnerId { get; set; }

    [ForeignKey(nameof(OwnerId))]
    public UserDB Owner { get; set; } = null!;

    [Column("alias_for_inbox_id")]
    public Guid? AliasForInboxId { get; set; }

    [ForeignKey(nameof(AliasForInboxId))]
    public InboxDB? AliasForInbox { get; set; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<FolderDB> Folders { get; set; } = [];
}
