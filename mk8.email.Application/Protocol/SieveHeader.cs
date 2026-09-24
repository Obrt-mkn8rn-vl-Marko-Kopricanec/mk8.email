namespace mk8.email.Application.Protocol;

internal sealed record SieveHeader(MatchOptions Options, IReadOnlyList<string> HeaderNames, IReadOnlyList<string> Keys) : SieveTest;
