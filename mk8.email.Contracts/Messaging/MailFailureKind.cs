namespace mk8.email.Contracts.Messaging;

public enum MailFailureKind
{
    None = 0,
    MalformedBatch = 1,
    UnsupportedFeature = 2,
    ResourceLimit = 3,
    InvalidSelection = 4,
}
