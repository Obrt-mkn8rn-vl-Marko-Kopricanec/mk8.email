namespace mk8.email.Gateway.Protocols.Ews;

internal sealed record GatewayEwsFolderReference(string Id, bool Distinguished, string? Mailbox, string? ChangeKey = null);
