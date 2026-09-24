namespace mk8.email.Application.Protocol;

internal sealed record SieveProgram(IReadOnlyList<SieveStatement> Statements);
