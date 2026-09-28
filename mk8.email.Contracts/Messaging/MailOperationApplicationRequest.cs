namespace mk8.email.Contracts.Messaging;

public sealed record MailOperationApplicationRequest(
    ProtocolAuthentication Authentication,
    MailOperationCommand Command);
