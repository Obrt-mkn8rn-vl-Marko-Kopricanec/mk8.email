namespace mk8.email.Infrastructure.Models;

public static class MailQueueRecipientStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string PermanentFailure = "permanent_failure";
    public const string Quarantined = "quarantined";
}
