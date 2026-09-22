using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Jmap;

public sealed class GatewayJmapClient(IGatewayApplicationTransport transport)
    : IGatewayJmapClient
{
    public Task<JmapApplicationResult> GetSessionAsync(
        JmapSessionApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapSessionGet, request, cancellationToken);

    public Task<JmapApplicationResult> ProcessApiRequestAsync(
        JmapApiApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapApiProcess, request, cancellationToken);

    public Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapUpload, request, cancellationToken);

    public Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapDownload, request, cancellationToken);

    public Task<JmapApplicationResult> PollEventAsync(
        JmapEventApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(ApplicationOperations.JmapEventPoll, request, cancellationToken);

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
