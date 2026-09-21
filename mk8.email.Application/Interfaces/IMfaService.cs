namespace mk8.email.Application.Interfaces;

public enum MfaVerificationResult
{
    NotRequired,
    Succeeded,
    Failed,
}

public sealed record MfaEnrollmentResult(
    bool Succeeded,
    string Message,
    string? Secret = null,
    string? ProvisioningUri = null);

public sealed record MfaRecoveryCodesResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string>? RecoveryCodes = null);

public sealed record MfaStatus(
    bool IsEnrolled,
    string? Name,
    DateTime? CreatedAt,
    DateTime? VerifiedAt,
    DateTime? LastUsedAt,
    int RemainingRecoveryCodes);

public interface IMfaService
{
    Task<MfaEnrollmentResult> BeginTotpEnrollmentAsync(
        string username,
        string name,
        CancellationToken cancellationToken = default);

    Task<MfaRecoveryCodesResult> ConfirmTotpEnrollmentAsync(
        string username,
        string code,
        CancellationToken cancellationToken = default);

    Task<MfaRecoveryCodesResult> RegenerateRecoveryCodesAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<bool> DisableTotpAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<MfaStatus> GetStatusAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<MfaVerificationResult> VerifyForAuthenticationAsync(
        Guid userId,
        string code,
        CancellationToken cancellationToken = default);
}
