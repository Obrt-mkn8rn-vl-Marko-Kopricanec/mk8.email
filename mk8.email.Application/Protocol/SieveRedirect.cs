namespace mk8.email.Application.Protocol;

internal sealed record SieveRedirect(string Address, bool Copy) : SieveStatement;
