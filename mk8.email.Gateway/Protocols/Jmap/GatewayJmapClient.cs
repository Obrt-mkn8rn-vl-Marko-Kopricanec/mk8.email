using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Jmap;

public sealed class GatewayJmapClient(IGatewayApplicationTransport transport)
    : IGatewayJmapClient
{
    public Task<JmapApplicationResult> GetProfileAsync(
        JmapProfileApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapProfileGet, request, cancellationToken);

    public Task<JmapApplicationResult> ValidatePlanAsync(
        MailPlanApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.MailPlanValidate, request, cancellationToken);

    public Task<JmapApplicationResult> ExecuteOperationAsync(
        MailOperationApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.MailOperationExecute, request, cancellationToken);

    public Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapUpload, request, cancellationToken);

    public Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapDownload, request, cancellationToken);

    public Task<JmapApplicationResult> PollChangesAsync(
        JmapChangesApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapChangesPoll, request, cancellationToken);

    private Task<JmapApplicationResult> SendAsync<TRequest>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TRequest, JmapApplicationResult>(
            "jmap",
            operation,
            request,
            cancellationToken);
}
