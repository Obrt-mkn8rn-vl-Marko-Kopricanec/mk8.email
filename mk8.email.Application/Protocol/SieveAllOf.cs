namespace mk8.email.Application.Protocol;

internal sealed record SieveAllOf(IReadOnlyList<SieveTest> Tests) : SieveTest;
