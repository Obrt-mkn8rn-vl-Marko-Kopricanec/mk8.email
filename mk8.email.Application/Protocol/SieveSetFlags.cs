namespace mk8.email.Application.Protocol;

internal sealed record SieveSetFlags(IReadOnlyList<string> Flags) : SieveStatement;
