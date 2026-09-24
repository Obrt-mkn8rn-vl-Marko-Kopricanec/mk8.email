namespace mk8.email.Application.Protocol;

internal sealed record SieveEnvelope(MatchOptions Options, SieveAddressPart AddressPart, IReadOnlyList<string> Fields, IReadOnlyList<string> Keys) : SieveTest;
