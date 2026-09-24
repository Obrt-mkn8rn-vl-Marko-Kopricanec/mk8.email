namespace mk8.email.Application.Protocol;

internal sealed record SieveIf(IReadOnlyList<SieveBranch> Branches, IReadOnlyList<SieveStatement> ElseStatements) : SieveStatement;
