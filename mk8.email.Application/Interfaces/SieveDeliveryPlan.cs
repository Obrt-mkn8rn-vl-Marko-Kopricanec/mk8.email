namespace mk8.email.Application.Interfaces;

internal sealed record SieveDeliveryPlan(
    bool ScriptApplied,
    IReadOnlyList<SieveDeliveryInstruction> Deliveries,
    IReadOnlyList<string> Redirects,
    string? RejectReason,
    bool Discarded);
