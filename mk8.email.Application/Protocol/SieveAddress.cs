namespace mk8.email.Application.Protocol;

internal sealed record SieveAddress(MatchOptions Options, SieveAddressPart AddressPart, IReadOnlyList<string> HeaderNames, IReadOnlyList<string> Keys) : SieveTest;
