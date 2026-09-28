using System.Text.Json.Serialization;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed record ApplicationArgumentBinding(
    string Target,
    string SourceCorrelationId,
    [property: JsonRequired] MailOperationKind SourceOperation,
    ApplicationValuePathSegment[] Path,
    ApplicationBindingFailure Failure = ApplicationBindingFailure.None);
