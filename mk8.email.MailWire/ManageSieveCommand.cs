namespace mk8.email.MailWire;

internal sealed record ManageSieveCommand(
    string Name,
    IReadOnlyList<ManageSieveToken> Arguments);
