using System.Security.Cryptography;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.TestSupport;

// Tests exercise the real Gateway sequencer without putting batching back in Worker.
internal sealed class ProcessorGatewayJmapClient(
    JmapRequestProcessor processor,
    AuthenticatedMailUser user,
    Guid? replayIdentity = null) : IGatewayJmapClient
{
    private int _step;

    public Task<JmapApplicationResult> GetProfileAsync(JmapProfileApplicationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<JmapApplicationResult> ValidatePlanAsync(MailPlanApplicationRequest request, CancellationToken cancellationToken = default)
    {
        processor.ValidatePreflight(request.Plan);
        return new(JmapApplicationOutcomes.Ok, Profile: await processor.GetProfileAsync(user, cancellationToken));
    }

    public async Task<JmapApplicationResult> ExecuteOperationAsync(MailOperationApplicationRequest request, CancellationToken cancellationToken = default)
    {
        var identity = Guid.CreateVersion7();
        if (replayIdentity is { } parent)
        {
            // Only this test adapter fixes queue identities to simulate redelivery.
            identity = ReplayOperationId(parent, _step);
        }
        _step++;
        return new(JmapApplicationOutcomes.Ok, OperationResult:
            await processor.ExecuteAsync(request.Command, user, identity, cancellationToken));
    }

    public Task<JmapApplicationResult> UploadAsync(JmapUploadApplicationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<JmapApplicationResult> DownloadAsync(JmapDownloadApplicationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<JmapApplicationResult> PollChangesAsync(JmapChangesApplicationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    internal static Guid ReplayOperationId(Guid parent, int step)
    {
        var key = parent.ToString("N") + ":" + step.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }
}
