namespace mk8.email.Application.Interfaces;

public sealed record MailScanResult(
    string Action,
    double Score,
    double RequiredScore,
    IReadOnlySet<string> Symbols,
    string AddedHeaders,
    bool IsMalware,
    bool IsTemporaryFailure);
