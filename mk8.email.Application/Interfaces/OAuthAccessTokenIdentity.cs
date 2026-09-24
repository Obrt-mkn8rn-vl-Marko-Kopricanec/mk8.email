namespace mk8.email.Application.Interfaces;

public sealed record OAuthAccessTokenIdentity(
    Guid UserId,
    string Username,
    IReadOnlyList<string> Scopes);
