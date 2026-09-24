namespace mk8.email.Application.Protocol;

internal sealed record SieveEvaluationResult(
    IReadOnlyList<SieveDelivery> Deliveries,
    IReadOnlyList<string> Redirects,
    string? RejectReason,
    bool Discarded);
