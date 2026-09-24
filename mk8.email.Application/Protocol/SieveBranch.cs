namespace mk8.email.Application.Protocol;

internal sealed record SieveBranch(SieveTest Test, IReadOnlyList<SieveStatement> Statements);
