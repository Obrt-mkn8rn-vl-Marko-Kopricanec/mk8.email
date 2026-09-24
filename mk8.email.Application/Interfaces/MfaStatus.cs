namespace mk8.email.Application.Interfaces;

public sealed record MfaStatus(
    bool IsEnrolled,
    string? Name,
    DateTime? CreatedAt,
    DateTime? VerifiedAt,
    DateTime? LastUsedAt,
    int RemainingRecoveryCodes);
