namespace mk8.email.Application.Interfaces;

public sealed record MfaEnrollmentResult(
    bool Succeeded,
    string Message,
    string? Secret = null,
    string? ProvisioningUri = null);
