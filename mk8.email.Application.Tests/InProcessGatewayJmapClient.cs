using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

internal sealed class InProcessGatewayJmapClient(IServiceScopeFactory scopes)
    : IGatewayJmapClient
{
    public Task<JmapApplicationResult> GetProfileAsync(
        JmapProfileApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.GetProfileAsync(request, cancellationToken));

    public Task<JmapApplicationResult> ExecuteBatchAsync(
        JmapBatchApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.ExecuteBatchAsync(request, cancellationToken));

    public Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.UploadAsync(request, cancellationToken));

    public Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.DownloadAsync(request, cancellationToken));

    public Task<JmapApplicationResult> PollChangesAsync(
        JmapChangesApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.PollChangesAsync(request, cancellationToken));

    private async Task<JmapApplicationResult> InvokeAsync(
        Func<IJmapApplicationService, Task<JmapApplicationResult>> operation)
    {
        using var scope = scopes.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IJmapApplicationService>());
    }
}
