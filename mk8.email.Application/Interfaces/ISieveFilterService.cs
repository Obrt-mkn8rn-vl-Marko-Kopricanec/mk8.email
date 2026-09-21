namespace mk8.email.Application.Interfaces;

internal sealed record SieveDeliveryInstruction(
    string Folder,
    IReadOnlyList<string> Flags,
    bool Create);

internal sealed record SieveDeliveryPlan(
    bool ScriptApplied,
    IReadOnlyList<SieveDeliveryInstruction> Deliveries,
    IReadOnlyList<string> Redirects,
    string? RejectReason,
    bool Discarded);

internal interface ISieveFilterService
{
    Task<SieveDeliveryPlan> EvaluateAsync(
        string envelopeSender,
        string recipient,
        string rawMessage,
        string defaultFolder,
        CancellationToken cancellationToken = default);
}
