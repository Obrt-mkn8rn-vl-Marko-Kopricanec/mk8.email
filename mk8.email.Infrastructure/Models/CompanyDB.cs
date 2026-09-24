using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("companies")]
public class CompanyDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<AddressDB> Addresses { get; set; } = [];
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2227", Justification = "The EF navigation remains settable for materialization and existing object initializers.")]
    public ICollection<UserDB> Users { get; set; } = [];
    public CompanyConfigDB? Config { get; set; }
    public CompanyLimitsDB? Limits { get; set; }
}
