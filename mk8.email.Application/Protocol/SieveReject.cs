namespace mk8.email.Application.Protocol;

internal sealed record SieveReject(string Reason) : SieveStatement;
