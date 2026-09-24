namespace mk8.email.Application.Interfaces;

public sealed record ApplicationPasswordSummary(
    Guid Id,
    string Name,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);
