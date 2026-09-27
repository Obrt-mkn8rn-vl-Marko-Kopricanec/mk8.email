using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailApplicationFailure(
    [property: JsonRequired] MailFailureKind Kind,
    string? Detail = null,
    MailResourceLimit Limit = MailResourceLimit.None);
