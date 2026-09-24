namespace mk8.email.Application.Protocol;

internal sealed record SieveBody(MatchOptions Options, SieveBodyTransform Transform, IReadOnlyList<string> ContentTypes, IReadOnlyList<string> Keys) : SieveTest;
