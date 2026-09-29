// Search-snippet commands and results are grouped as one internal transport.
#pragma warning disable MA0048
using System.Text.Json.Serialization;

namespace mk8.email.Contracts.Messaging;

public sealed record MailSearchSnippetCommand(
    [property: JsonRequired] Guid AccountId,
    [property: JsonRequired] bool CheckAccountOnly,
    [property: JsonRequired] IReadOnlyList<Guid> MessageIds,
    [property: JsonRequired] IReadOnlyList<string> Terms);

public enum MailSearchSnippetStatus
{
    Ok,
    AccountNotFound,
    Authorized,
}

public sealed record MailSearchSnippetSnapshot(
    [property: JsonRequired] Guid MessageId,
    [property: JsonRequired] string? SubjectText,
    [property: JsonRequired] string? PreviewWindow);

public sealed record MailSearchSnippetResult(
    [property: JsonRequired] MailSearchSnippetStatus Status,
    [property: JsonRequired] IReadOnlyList<MailSearchSnippetSnapshot> Snippets);
