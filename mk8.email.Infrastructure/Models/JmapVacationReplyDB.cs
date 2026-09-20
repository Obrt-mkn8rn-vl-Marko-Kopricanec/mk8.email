using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace mk8.email.Infrastructure.Models;

[Table("jmap_vacation_replies")]
public sealed class JmapVacationReplyDB
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("account_id")]
    public Guid AccountId { get; set; }

    [Required]
    [MaxLength(320)]
    [Column("sender_address")]
    public string SenderAddress { get; set; } = string.Empty;

    [Column("last_delivery_id")]
    public Guid LastDeliveryId { get; set; }

    [Column("last_sent_at")]
    public DateTime LastSentAt { get; set; }
}
