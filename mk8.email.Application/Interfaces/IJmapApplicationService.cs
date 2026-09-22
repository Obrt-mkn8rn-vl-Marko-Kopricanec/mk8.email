using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Interfaces;

public interface IJmapApplicationService
{
    Task<JmapApplicationResult> GetSessionAsync(
        JmapSessionApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> ProcessApiRequestAsync(
        JmapApiApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> UploadAsync(
        JmapUploadApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> DownloadAsync(
        JmapDownloadApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<JmapApplicationResult> PollEventAsync(
        JmapEventApplicationRequest request,
        CancellationToken cancellationToken = default);
}
