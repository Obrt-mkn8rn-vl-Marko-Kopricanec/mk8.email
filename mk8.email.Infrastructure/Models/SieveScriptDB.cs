using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("sieve_scripts")]
public sealed class SieveScriptDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public UserDB User { get; set; } = null!;

    [Required]
    [MaxLength(512)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("content")]
    public string? Content { get; set; }

    [Column("size_bytes")]
    public int SizeBytes { get; set; }

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

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
