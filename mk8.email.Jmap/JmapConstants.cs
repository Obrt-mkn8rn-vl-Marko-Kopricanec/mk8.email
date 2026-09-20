namespace mk8.email.Jmap;

public static class JmapConstants
{
    public const string CoreCapability = "urn:ietf:params:jmap:core";
    public const string MailCapability = "urn:ietf:params:jmap:mail";
    public const string SubmissionCapability = "urn:ietf:params:jmap:submission";
    public const string VacationResponseCapability = "urn:ietf:params:jmap:vacationresponse";

    public const string MailboxDataType = "Mailbox";
    public const string ThreadDataType = "Thread";
    public const string EmailDataType = "Email";
    public const string EmailDeliveryDataType = "EmailDelivery";
    public const string IdentityDataType = "Identity";
    public const string EmailSubmissionDataType = "EmailSubmission";
    public const string VacationResponseDataType = "VacationResponse";
    public const string PushSubscriptionDataType = "PushSubscription";

    public const string CreatedChange = "created";
    public const string UpdatedChange = "updated";
    public const string DestroyedChange = "destroyed";
}
