namespace mk8.email.Application.Interfaces;

// The provisioning URI is an exact otpauth payload in the published response contract.
// Parsing it as System.Uri would normalize or reject values before the client receives them.
#pragma warning disable CA1054, CA1056
public sealed record MfaEnrollmentResult(
    bool Succeeded,
    string Message,
    string? Secret = null,
    string? ProvisioningUri = null);
#pragma warning restore CA1054, CA1056
