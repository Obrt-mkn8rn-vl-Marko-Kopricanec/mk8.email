namespace mk8.email.Application.Protocol;

internal sealed record SieveAnyOf(IReadOnlyList<SieveTest> Tests) : SieveTest;
