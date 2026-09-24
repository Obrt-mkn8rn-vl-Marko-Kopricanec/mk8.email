namespace mk8.email.Application.Interfaces;

public sealed record OAuthGrantSummary(
    Guid Id,
    string ClientId,
    string DeviceName,
    IReadOnlyList<string> Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);
