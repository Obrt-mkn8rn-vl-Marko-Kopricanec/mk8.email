namespace mk8.email.Application.Protocol;

internal sealed record SieveExists(IReadOnlyList<string> HeaderNames) : SieveTest;
