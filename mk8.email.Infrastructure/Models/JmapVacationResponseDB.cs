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

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
