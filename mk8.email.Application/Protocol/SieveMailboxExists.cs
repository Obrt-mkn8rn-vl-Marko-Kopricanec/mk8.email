namespace mk8.email.Application.Protocol;

internal sealed record SieveMailboxExists(IReadOnlyList<string> Folders) : SieveTest;
