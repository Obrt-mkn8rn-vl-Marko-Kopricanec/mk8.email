namespace mk8.email.Application.Protocol;

internal sealed record SieveAddFlags(IReadOnlyList<string> Flags) : SieveStatement;
