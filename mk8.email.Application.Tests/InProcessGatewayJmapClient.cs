using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;

namespace mk8.email.Application.Tests;

internal sealed class InProcessGatewayJmapClient(IServiceScopeFactory scopes)
    : IGatewayJmapClient
{
    public Task<JmapApplicationResult> GetSessionAsync(
        JmapSessionApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.GetSessionAsync(request, cancellationToken));

    public Task<JmapApplicationResult> ProcessApiRequestAsync(
        JmapApiApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.ProcessApiRequestAsync(request, cancellationToken));

    public Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.UploadAsync(request, cancellationToken));

    public Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.DownloadAsync(request, cancellationToken));

    public Task<JmapApplicationResult> PollEventAsync(
        JmapEventApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(service => service.PollEventAsync(request, cancellationToken));

    private async Task<JmapApplicationResult> InvokeAsync(
        Func<IJmapApplicationService, Task<JmapApplicationResult>> operation)
    {
        using var scope = scopes.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IJmapApplicationService>());
    }
}
