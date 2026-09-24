namespace mk8.email.Application.Protocol;

internal sealed record SieveKeep(IReadOnlyList<string>? Flags) : SieveStatement;
