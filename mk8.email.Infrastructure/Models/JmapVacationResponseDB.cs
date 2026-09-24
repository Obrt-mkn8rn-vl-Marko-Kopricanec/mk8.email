using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_vacation_responses")]
public sealed class JmapVacationResponseDB
{
    [Key]
    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; }

    [Column("from_date")]
    public DateTime? FromDate { get; set; }

    [Column("to_date")]
    public DateTime? ToDate { get; set; }

    [MaxLength(998)]
    [Column("subject")]
    public string? Subject { get; set; }

    [Column("text_body")]
    public string? TextBody { get; set; }

    [Column("html_body")]
    public string? HtmlBody { get; set; }

    [Column("body_size_bytes")]
    public int BodySizeBytes { get; set; }

    [MaxLength(32)]
    [Column("body_object_provider")]
    public string? BodyObjectProvider { get; set; }

    [MaxLength(1024)]
    [Column("body_object_name")]
    public string? BodyObjectName { get; set; }

    [MaxLength(64)]
    [Column("body_object_sha256")]
    public string? BodyObjectSha256 { get; set; }

    [MaxLength(256)]
    [Column("body_object_etag")]
    public string? BodyObjectEntityTag { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
