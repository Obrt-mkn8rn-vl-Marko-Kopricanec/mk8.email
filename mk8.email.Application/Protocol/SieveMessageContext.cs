namespace mk8.email.Application.Protocol;

internal sealed record SieveMessageContext(
    string EnvelopeSender,
    string EnvelopeRecipient,
    string RawMessage,
    string DefaultFolder,
    IReadOnlySet<string> Mailboxes);
