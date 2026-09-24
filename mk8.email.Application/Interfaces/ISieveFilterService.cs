namespace mk8.email.Application.Interfaces;

internal interface ISieveFilterService
{
    Task<SieveDeliveryPlan> EvaluateAsync(
        string envelopeSender,
        string recipient,
        string rawMessage,
        string defaultFolder,
        CancellationToken cancellationToken = default);
}
