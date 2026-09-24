namespace mk8.email.Application.Interfaces;

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
