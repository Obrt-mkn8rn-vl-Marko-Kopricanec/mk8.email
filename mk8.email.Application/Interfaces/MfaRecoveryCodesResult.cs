namespace mk8.email.Application.Interfaces;

public sealed record MfaRecoveryCodesResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string>? RecoveryCodes = null);
